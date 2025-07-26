using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using Microsoft.Win32;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeejNG.Dialogs;
using DeejNG.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using DeejNG.Classes;
using DeejNG.Models;
using System.Runtime.InteropServices;
using System.Windows.Media;
using DeejNG.Views;

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        #region Public Fields

        public List<ChannelControl> _channelControls = new();
        public FloatingOverlay _overlay;

        #endregion Public Fields

        #region Private Fields

        // Object pools to reduce allocations (UNUSED - can be removed)
        private readonly Queue<List<AudioTarget>> _audioTargetListPool = new();
        private readonly HashSet<string> _cachedMappedApplications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private readonly HashSet<string> _tempStringSet = new(StringComparer.OrdinalIgnoreCase);
        // Cached collections to avoid repeated allocations
        private readonly List<AudioTarget> _tempTargetList = new();
        private readonly object _unmappedLock = new object();
        private readonly TimeSpan UNMAPPED_THROTTLE_INTERVAL = TimeSpan.FromMilliseconds(100);

        // 500ms calibration period
        private bool _allowVolumeApplication = false;
        private MMDevice _audioDevice;
        private AudioService _audioService;
        private MMDevice _cachedAudioDevice;
        private SessionCollection _cachedSessionsForMeters;
        private bool _disableSmoothing = false;
        private int _expectedSliderCount = -1;
        private bool _hasLoadedInitialSettings = false;
        private bool _hasSyncedMuteStates = false;
        private bool _isClosing = false;
        private bool _isInitializing = true;
        private DateTime _lastDeviceCacheTime = DateTime.MinValue;
        private DateTime _lastForcedCleanup = DateTime.MinValue;
        private DateTime _lastMeterSessionRefresh = DateTime.MinValue;
        private DateTime _lastUnmappedMeterUpdate = DateTime.MinValue;
        private bool _metersEnabled = true;
        private Dictionary<string, IAudioSessionEventsHandler> _registeredHandlers = new();
        private int _sessionCacheHitCount = 0;
        private List<(AudioSessionControl session, string sessionId, string instanceId)> _sessionIdCache = new();
        private AudioEndpointVolume _systemVolume;
        private bool isDarkTheme = false;

        // New Manager Fields
        private readonly DeviceCacheManager _deviceManager;
        private readonly AppSettingsManager _settingsManager;
        private readonly SerialConnectionManager _serialManager;
        private readonly TimerCoordinator _timerCoordinator;

        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            _isInitializing = true;

            InitializeComponent();
            Loaded += MainWindow_Loaded;

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");

            // Initialize managers
            _deviceManager = new DeviceCacheManager();
            _settingsManager = new AppSettingsManager();
            _serialManager = new SerialConnectionManager();
            _timerCoordinator = new TimerCoordinator();

            _audioService = new AudioService();

            // Load the saved port name from settings BEFORE populating ports
            LoadSavedPortName();

            // Load ports AFTER loading the saved port name so ComboBox can select the correct port
            LoadAvailablePorts();

            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _systemVolume = _audioDevice.AudioEndpointVolume;
            _systemVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;

            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            // Initialize timers
            _timerCoordinator.InitializeTimers();
            _timerCoordinator.MeterUpdate += UpdateMeters;
            _timerCoordinator.SessionCacheUpdate += SessionCacheTimer_Tick;
            _timerCoordinator.ForceCleanup += ForceCleanupTimer_Tick;
            _timerCoordinator.SerialReconnectAttempt += SerialReconnectTimer_Tick;
            _timerCoordinator.SerialWatchdogCheck += SerialWatchdogTimer_Tick;
            _timerCoordinator.PositionSave += PositionSaveTimer_Tick;

            // Setup serial manager events
            _serialManager.DataReceived += HandleSliderData;
            _serialManager.Connected += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateConnectionStatus();

                    // Update ComboBox to show the connected port
                    if (!string.IsNullOrEmpty(_serialManager.CurrentPort))
                    {
                        ComPortSelector.SelectedItem = _serialManager.CurrentPort;
                    }

                    // Stop reconnection timer on successful connection
                    _timerCoordinator.StopSerialReconnect();
                });
            };
            _serialManager.Disconnected += () =>
            {
                _allowVolumeApplication = false;
                Dispatcher.Invoke(() =>
                {
                    UpdateConnectionStatus();

                    // Start reconnection timer only if not manually disconnected
                    if (_serialManager.ShouldAttemptReconnect())
                    {
                        _timerCoordinator.StartSerialReconnect();
                    }
                });
            };

            StartSessionCacheUpdater();

            // Start timers
            _timerCoordinator.StartSerialWatchdog();

            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            IconHandler.AddIconToRemovePrograms("DeejNG");
            SetDisplayIcon();

            // Load settings but don't auto-connect to serial port yet
            LoadSettingsWithoutSerialConnection();

            _isInitializing = false;
            if (_settingsManager.Settings.StartMinimized)
            {
                WindowState = WindowState.Minimized;
                Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }

            _timerCoordinator.StartForceCleanup();

            // Setup automatic serial connection
            SetupAutomaticSerialConnection();
        }

        #endregion Public Constructors

        #region Public Methods

        /// <summary>
        /// Finds the ChannelControl that currently controls the specified target
        /// </summary>
        /// <param name="targetName">The target application name</param>
        /// <returns>The control managing this target, or null if not found</returns>
        public ChannelControl FindControlForTarget(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName)) return null;

            string normalizedTarget = targetName.ToLowerInvariant();

            foreach (var control in _channelControls)
            {
                foreach (var target in control.AudioTargets)
                {
                    if (!target.IsInputDevice &&
                        string.Equals(target.Name, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return control;
                    }
                }
            }

            return null;
        }

        public HashSet<string> GetAllMappedApplications()
        {
            // Reuse the temp set instead of creating new ones
            _tempStringSet.Clear();

            foreach (var control in _channelControls)
            {
                foreach (var target in control.AudioTargets)
                {
                    if (!target.IsInputDevice && !string.IsNullOrEmpty(target.Name))
                    {
                        _tempStringSet.Add(target.Name.ToLowerInvariant());
                    }
                }
            }

            // Return a copy to avoid modification issues
            return new HashSet<string>(_tempStringSet, StringComparer.OrdinalIgnoreCase);
        }

        public string GetChannelLabel(ChannelControl control)
        {
            if (control.AudioTargets != null && control.AudioTargets.Count > 0)
            {
                var primaryTarget = control.AudioTargets.FirstOrDefault();
                if (primaryTarget != null && !string.IsNullOrEmpty(primaryTarget.Name))
                {
                    string name = primaryTarget.Name;

                    // Handle special cases
                    if (name.Equals("system", StringComparison.OrdinalIgnoreCase))
                        return "System";
                    if (name.Equals("unmapped", StringComparison.OrdinalIgnoreCase))
                        return "Unmapped";

                    // For regular app names, return the full name (wrapping will handle display)
                    return char.ToUpper(name[0]) + name.Substring(1).ToLower();
                }
            }

            // Fallback to channel number
            int channelIndex = _channelControls.IndexOf(control);
            return $"Channel {channelIndex + 1}";
        }

        public List<string> GetCurrentTargets()
        {
            return _channelControls
                .Select(c => c.TargetExecutable?.Trim().ToLowerInvariant())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Handles session disconnection events from the decoupled handlers
        /// </summary>
        /// <param name="targetName">The target that disconnected</param>
        public void HandleSessionDisconnected(string targetName)
        {
            try
            {
                // Clean up the handler registration
                if (_registeredHandlers.TryGetValue(targetName, out var handler))
                {
                    _registeredHandlers.Remove(targetName);
                    Debug.WriteLine($"[MainWindow] Removed handler for disconnected session: {targetName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] Error handling session disconnect for {targetName}: {ex.Message}");
            }
        }

        public void ShowVolumeOverlay()
        {
            Debug.WriteLine("[Overlay] ShowVolumeOverlay triggered");

            if (_settingsManager.Settings is null || !_settingsManager.Settings.OverlayEnabled)
            {
                Debug.WriteLine("[Overlay] Disabled in settings");
                return;
            }

            if (_overlay == null)
            {
                Debug.WriteLine("[Overlay] Creating new overlay");
                Debug.WriteLine($"[Overlay] Loaded position from settings: ({_settingsManager.Settings.OverlayX}, {_settingsManager.Settings.OverlayY})");

                // Validate position against virtual screen bounds (multi-monitor)
                if (!_settingsManager.IsPositionValid(_settingsManager.Settings.OverlayX, _settingsManager.Settings.OverlayY))
                {
                    Debug.WriteLine($"[Overlay] Loaded position ({_settingsManager.Settings.OverlayX}, {_settingsManager.Settings.OverlayY}) is invalid, using default");
                    _settingsManager.Settings.OverlayX = 100;
                    _settingsManager.Settings.OverlayY = 100;
                }

                _overlay = new FloatingOverlay(_settingsManager.Settings, this);
                Debug.WriteLine($"[Overlay] Created at validated position ({_settingsManager.Settings.OverlayX}, {_settingsManager.Settings.OverlayY})");
            }

            var volumes = _channelControls.Select(c => c.CurrentVolume).ToList();
            var labels = _channelControls.Select(c => GetChannelLabel(c)).ToList();

            _overlay.ShowVolumes(volumes, labels);
        }

        public void UpdateOverlayPosition(double x, double y)
        {
            if (_settingsManager.Settings != null)
            {
                // Store precise position with higher precision
                _settingsManager.Settings.OverlayX = Math.Round(x, 1);
                _settingsManager.Settings.OverlayY = Math.Round(y, 1);

                Debug.WriteLine($"[Overlay] Position updated: X={_settingsManager.Settings.OverlayX}, Y={_settingsManager.Settings.OverlayY}");

                // Debounce saves using timer coordinator
                _timerCoordinator.TriggerPositionSave();
            }
        }

        public void UpdateOverlaySettings(AppSettings newSettings)
        {
            // Preserve current position if overlay exists and is visible
            if (_overlay != null && _overlay.IsVisible)
            {
                var currentX = Math.Round(_overlay.Left, 1);
                var currentY = Math.Round(_overlay.Top, 1);

                _settingsManager.Settings.OverlayX = currentX;
                _settingsManager.Settings.OverlayY = currentY;
            }
            else if (_settingsManager.IsPositionValid(newSettings.OverlayX, newSettings.OverlayY))
            {
                _settingsManager.Settings.OverlayX = newSettings.OverlayX;
                _settingsManager.Settings.OverlayY = newSettings.OverlayY;
            }
            else
            {
                _settingsManager.Settings.OverlayX = 100;
                _settingsManager.Settings.OverlayY = 100;
            }

            // Update all settings including text color
            _settingsManager.Settings.OverlayEnabled = newSettings.OverlayEnabled;
            _settingsManager.Settings.OverlayOpacity = newSettings.OverlayOpacity;
            _settingsManager.Settings.OverlayTimeoutSeconds = newSettings.OverlayTimeoutSeconds;
            _settingsManager.Settings.OverlayTextColor = newSettings.OverlayTextColor;

            Debug.WriteLine($"[Overlay] Settings updated - Text Color: {_settingsManager.Settings.OverlayTextColor}");
        }

        #endregion Public Methods

        #region Protected Methods

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;

            // Stop all timers first
            _timerCoordinator.StopAll();

            // Close the overlay if it exists
            if (_overlay != null)
            {
                _overlay.Close();
                _overlay = null;
            }

            // Clean up all registered event handlers
            foreach (var target in _registeredHandlers.Keys.ToList())
            {
                try
                {
                    _registeredHandlers.Remove(target);
                    Debug.WriteLine($"[Cleanup] Unregistered event handler for {target} on application close");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Failed to unregister handler for {target} on close: {ex.Message}");
                }
            }

            // Dispose managers
            _serialManager?.Dispose();
            _deviceManager?.Dispose();
            _timerCoordinator?.Dispose();

            // Unsubscribe from all events
            try
            {
                if (_systemVolume != null)
                {
                    _systemVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                }
            }
            catch { }

            // Clean up all COM objects
            foreach (var sessionTuple in _sessionIdCache)
            {
                try
                {
                    if (sessionTuple.session != null)
                    {
                        int refCount = Marshal.FinalReleaseComObject(sessionTuple.session);
                        Debug.WriteLine($"[Cleanup] Released session COM object, final ref count: {refCount}");
                    }
                }
                catch { }
            }
            _sessionIdCache.Clear();

            // Force final garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            base.OnClosed(e);
        }

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

        #endregion Protected Methods

        #region Private Methods

        private static void SetDisplayIcon()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (!System.IO.File.Exists(exePath))
                {
                    return;
                }

                var myUninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                string[]? mySubKeyNames = myUninstallKey?.GetSubKeyNames();
                for (int i = 0; i < mySubKeyNames?.Length; i++)
                {
                    RegistryKey? myKey = myUninstallKey?.OpenSubKey(mySubKeyNames[i], true);
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

        private void ApplyTheme(string theme)
        {
            try
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Applying theme '{theme}': {ex.Message}");
            }
        }

        private void ApplyVolumeToTargets(ChannelControl ctrl, List<AudioTarget> targets, float level)
        {
            // Simple check - don't apply volumes until we're ready
            if (!_allowVolumeApplication)
            {
                return;
            }

            foreach (var target in targets)
            {
                try
                {
                    if (target.IsInputDevice)
                    {
                        _deviceManager.ApplyInputDeviceVolume(target.Name, level, ctrl.IsMuted);
                    }
                    else if (target.IsOutputDevice)
                    {
                        _deviceManager.ApplyOutputDeviceVolume(target.Name, level, ctrl.IsMuted);
                    }
                    else if (string.Equals(target.Name, "unmapped", StringComparison.OrdinalIgnoreCase))
                    {
                        // Handle unmapped applications with aggressive throttling
                        lock (_unmappedLock)
                        {
                            // More aggressive throttling for unmapped
                            var now = DateTime.Now;
                            if ((now - _lastUnmappedMeterUpdate) < UNMAPPED_THROTTLE_INTERVAL)
                            {
                                // Skip this update entirely for responsiveness
                            }
                            _lastUnmappedMeterUpdate = now;

                            var mappedApps = GetAllMappedApplications();
                            mappedApps.Remove("unmapped"); // Don't exclude unmapped from itself

                            _audioService.ApplyVolumeToUnmappedApplications(level, ctrl.IsMuted, mappedApps);
                        }
                    }
                    else
                    {
                        _audioService.ApplyVolumeToTarget(target.Name, level, ctrl.IsMuted);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Applying volume to {target.Name}: {ex.Message}");
                }
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
            });
        }

        private void CleanupEventHandlers()
        {
            try
            {
                var handlersToRemove = new List<string>();

                foreach (var kvp in _registeredHandlers)
                {
                    try
                    {
                        var currentTargets = GetCurrentTargets();
                        if (!currentTargets.Contains(kvp.Key))
                        {
                            handlersToRemove.Add(kvp.Key);
                        }
                    }
                    catch
                    {
                        handlersToRemove.Add(kvp.Key);
                    }
                }

                foreach (var target in handlersToRemove)
                {
                    _registeredHandlers.Remove(target);
                    Debug.WriteLine($"[Cleanup] Removed stale event handler for {target}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cleanup] Error cleaning event handlers: {ex.Message}");
            }
        }

        private void ComPortSelector_DropDownOpened(object sender, EventArgs e)
        {
            Debug.WriteLine("[UI] COM port dropdown opened, refreshing ports...");
            LoadAvailablePorts();
        }

        private void ComPortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComPortSelector.SelectedItem is string selectedPort)
            {
                _serialManager.SetUserSelectedPort(selectedPort);
            }
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_serialManager.IsConnected)
            {
                // User wants to disconnect manually
                _timerCoordinator.StopSerialReconnect();
                _serialManager.ManualDisconnect();
            }
            else
            {
                // User wants to connect to selected port
                if (ComPortSelector.SelectedItem is string selectedPort)
                {
                    Debug.WriteLine($"[Manual] User clicked connect for port: {selectedPort}");

                    // Update button state immediately
                    ConnectButton.IsEnabled = false;
                    ConnectButton.Content = "Connecting...";

                    // Stop automatic reconnection while user is manually connecting
                    _timerCoordinator.StopSerialReconnect();

                    // Try connection
                    _serialManager.InitSerial(selectedPort, 9600);

                    // Reset button after short delay
                    var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    resetTimer.Tick += (s, args) =>
                    {
                        resetTimer.Stop();
                        UpdateConnectionStatus();
                    };
                    resetTimer.Start();
                }
                else
                {
                    MessageBox.Show("Please select a COM port first.", "No Port Selected",
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
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
                contextMenu.Items.Add(new Separator());
                contextMenu.Items.Add(exitMenuItem);

                MyNotifyIcon.ContextMenu = contextMenu;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Creating notify icon context menu: {ex.Message}");
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

        private void EnableStartup()
        {
            string appName = "DeejNG";

            try
            {
                string shortcutPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs",
                    "Jimmy White",
                    "DeejNG",
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

        private async void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void ForceCleanupTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing) return;

            try
            {
                Debug.WriteLine("[ForceCleanup] Starting aggressive cleanup...");
                _audioService?.ForceCleanup();
                AudioUtilities.ForceCleanup();

                CleanupEventHandlers();

                // Refresh device cache if needed
                if (_deviceManager.ShouldRefreshCache())
                {
                    _deviceManager.RefreshCaches();
                }

                // Force COM object cleanup
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                _lastForcedCleanup = DateTime.Now;
                Debug.WriteLine("[ForceCleanup] Cleanup completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ForceCleanup] Error: {ex.Message}");
            }
        }

        private void GenerateSliders(int count)
        {
            SliderPanel.Children.Clear();
            _channelControls.Clear();

            var savedSettings = _settingsManager.LoadSettingsFromDisk();
            var savedTargetGroups = savedSettings?.SliderTargets ?? new List<List<AudioTarget>>();

            _isInitializing = true;
            _allowVolumeApplication = false;

            Debug.WriteLine("[Init] Generating sliders - ALL volume operations DISABLED until first data");

            for (int i = 0; i < count; i++)
            {
                var control = new ChannelControl();

                // Set targets for this control
                List<AudioTarget> targetsForThisControl;

                if (i < savedTargetGroups.Count && savedTargetGroups[i].Count > 0)
                {
                    targetsForThisControl = savedTargetGroups[i];
                }
                else
                {
                    targetsForThisControl = new List<AudioTarget>();

                    if (i == 0)
                    {
                        targetsForThisControl.Add(new AudioTarget
                        {
                            Name = "system",
                            IsInputDevice = false
                        });
                    }
                }

                control.AudioTargets = targetsForThisControl;
                control.SetMuted(false);

                control.TargetChanged += (_, _) => SaveSettings();

                control.VolumeOrMuteChanged += (targets, vol, mute) =>
                {
                    if (!_allowVolumeApplication) return;
                    ApplyVolumeToTargets(control, targets, vol);
                };

                control.SessionDisconnected += (sender, target) =>
                {
                    Debug.WriteLine($"[MainWindow] Received session disconnected for {target}");
                    if (_registeredHandlers.TryGetValue(target, out var handler))
                    {
                        _registeredHandlers.Remove(target);
                        Debug.WriteLine($"[MainWindow] Removed handler for disconnected session: {target}");
                    }
                };

                _channelControls.Add(control);
                SliderPanel.Children.Add(control);
            }

            SetMeterVisibilityForAll(ShowSlidersCheckBox.IsChecked ?? true);

            Debug.WriteLine("[Init] Sliders generated, waiting for first hardware data before completing initialization");
        }

        private void HandleSliderData(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                // Validate data format before processing
                if (!data.Contains('|') && !float.TryParse(data, out _))
                {
                    Debug.WriteLine($"[Serial] Invalid data format: {data}");
                    return;
                }

                string[] parts = data.Split('|');

                if (parts.Length == 0)
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Hardware slider count takes priority - regenerate if mismatch
                        if (_channelControls.Count != parts.Length)
                        {
                            Debug.WriteLine($"[INFO] Hardware has {parts.Length} sliders but app has {_channelControls.Count}. Adjusting to match hardware.");

                            // Save current targets before regenerating
                            var currentTargets = _channelControls.Select(c => c.AudioTargets).ToList();

                            _expectedSliderCount = parts.Length;
                            GenerateSliders(parts.Length);

                            // Restore saved targets for sliders that still exist
                            for (int i = 0; i < Math.Min(currentTargets.Count, _channelControls.Count); i++)
                            {
                                _channelControls[i].AudioTargets = currentTargets[i];
                            }

                            SaveSettings();
                            return;
                        }

                        // Process the data for existing sliders only
                        int maxIndex = Math.Min(parts.Length, _channelControls.Count);

                        for (int i = 0; i < maxIndex; i++)
                        {
                            if (!float.TryParse(parts[i].Trim(), out float level)) continue;

                            // Handle hardware noise - snap small values to exact zero
                            if (level <= 10) level = 0;
                            if (level >= 1013) level = 1023;

                            level = Math.Clamp(level / 1023f, 0f, 1f);
                            if (InvertSliderCheckBox.IsChecked ?? false)
                                level = 1f - level;

                            var ctrl = _channelControls[i];
                            var targets = ctrl.AudioTargets;

                            if (targets.Count == 0) continue;

                            float currentVolume = ctrl.CurrentVolume;
                            if (Math.Abs(currentVolume - level) < 0.01f) continue;

                            ctrl.SmoothAndSetVolume(level, suppressEvent: !_allowVolumeApplication, disableSmoothing: _disableSmoothing);

                            if (_allowVolumeApplication)
                            {
                                ApplyVolumeToTargets(ctrl, targets, level);
                                ShowVolumeOverlay();
                            }
                        }

                        // Enable volume application after first successful data processing
                        if (!_allowVolumeApplication)
                        {
                            _allowVolumeApplication = true;
                            _isInitializing = false;

                            Debug.WriteLine("[Init] First data received - enabling volume application and completing setup");

                            SyncMuteStates();

                            if (!_timerCoordinator.IsMetersRunning)
                            {
                                _timerCoordinator.StartMeters();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] Processing slider data in UI thread: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Parsing slider data: {ex.Message}");
            }
        }

        private void LoadAvailablePorts()
        {
            try
            {
                var availablePorts = SerialPort.GetPortNames();
                string currentSelection = ComPortSelector.SelectedItem as string;

                ComPortSelector.ItemsSource = availablePorts;

                Debug.WriteLine($"[Ports] Found {availablePorts.Length} ports: [{string.Join(", ", availablePorts)}]");

                // Try to restore previous selection first
                if (!string.IsNullOrEmpty(currentSelection) && availablePorts.Contains(currentSelection))
                {
                    ComPortSelector.SelectedItem = currentSelection;
                    Debug.WriteLine($"[Ports] Restored previous selection: {currentSelection}");
                }
                else if (!string.IsNullOrEmpty(_serialManager.LastConnectedPort) && availablePorts.Contains(_serialManager.LastConnectedPort))
                {
                    ComPortSelector.SelectedItem = _serialManager.LastConnectedPort;
                    Debug.WriteLine($"[Ports] Selected saved port: {_serialManager.LastConnectedPort}");
                }
                else if (!string.IsNullOrEmpty(_settingsManager.Settings.PortName) && availablePorts.Contains(_settingsManager.Settings.PortName))
                {
                    ComPortSelector.SelectedItem = _settingsManager.Settings.PortName;
                    Debug.WriteLine($"[Ports] Selected settings port: {_settingsManager.Settings.PortName}");
                }
                else if (availablePorts.Length > 0)
                {
                    ComPortSelector.SelectedIndex = 0;
                    Debug.WriteLine($"[Ports] Selected first available port: {availablePorts[0]}");
                }
                else
                {
                    ComPortSelector.SelectedIndex = -1;
                    Debug.WriteLine("[Ports] No ports available");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to load available ports: {ex.Message}");
                ComPortSelector.ItemsSource = new string[0];
                ComPortSelector.SelectedIndex = -1;
            }
        }

        private void LoadSavedPortName()
        {
            try
            {
                string savedPort = _settingsManager.LoadSavedPortName();
                if (!string.IsNullOrWhiteSpace(savedPort))
                {
                    Debug.WriteLine($"[Settings] Loaded saved port name: {savedPort}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to load saved port name: {ex.Message}");
            }
        }

        private void LoadSettingsWithoutSerialConnection()
        {
            try
            {
                var settings = _settingsManager.LoadSettingsFromDisk();

                // Apply UI settings
                ApplyTheme(settings.IsDarkTheme ? "Dark" : "Light");
                InvertSliderCheckBox.IsChecked = settings.IsSliderInverted;
                ShowSlidersCheckBox.IsChecked = settings.VuMeters;

                SetMeterVisibilityForAll(settings.VuMeters);
                DisableSmoothingCheckBox.IsChecked = settings.DisableSmoothing;

                _settingsManager.ValidateOverlayPosition();

                // Handle startup settings
                StartOnBootCheckBox.Checked -= StartOnBootCheckBox_Checked;
                StartOnBootCheckBox.Unchecked -= StartOnBootCheckBox_Unchecked;

                bool isInStartup = IsStartupEnabled();
                settings.StartOnBoot = isInStartup;
                StartOnBootCheckBox.IsChecked = isInStartup;

                StartOnBootCheckBox.Checked += StartOnBootCheckBox_Checked;
                StartOnBootCheckBox.Unchecked += StartOnBootCheckBox_Unchecked;

                StartMinimizedCheckBox.IsChecked = settings.StartMinimized;
                StartMinimizedCheckBox.Checked += StartMinimizedCheckBox_Checked;

                // Generate sliders from saved settings
                if (settings.SliderTargets != null && settings.SliderTargets.Count > 0)
                {
                    _expectedSliderCount = settings.SliderTargets.Count;
                    GenerateSliders(settings.SliderTargets.Count);
                }
                else
                {
                    _expectedSliderCount = 4;
                    GenerateSliders(4);
                }

                _hasLoadedInitialSettings = true;

                foreach (var ctrl in _channelControls)
                    ctrl.SetMeterVisibility(settings.VuMeters);

                // If overlay is enabled and autohide is disabled, show it immediately after startup
                if (settings.OverlayEnabled && settings.OverlayTimeoutSeconds == AppSettings.OverlayNoTimeout)
                {
                    var startupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                    startupTimer.Tick += (s, e) =>
                    {
                        startupTimer.Stop();
                        ShowVolumeOverlay();
                        Debug.WriteLine("[Startup] Overlay shown automatically (autohide disabled)");
                    };
                    startupTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to load settings without serial: {ex.Message}");
                _expectedSliderCount = 4;
                GenerateSliders(4);
                _hasLoadedInitialSettings = true;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SliderScrollViewer.Visibility = Visibility.Visible;
            StartOnBootCheckBox.IsChecked = _settingsManager.Settings.StartOnBoot;
        }

        private void PositionSaveTimer_Tick(object sender, EventArgs e)
        {
            // Save to disk on background thread
            Task.Run(() =>
            {
                try
                {
                    _settingsManager.SaveSettingsAsync(_settingsManager.Settings);
                    Debug.WriteLine("[Overlay] Position saved to disk (debounced)");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Failed to save overlay position: {ex.Message}");
                }
            });
        }

        private void SaveSettings()
        {
            if (_isInitializing || !_hasLoadedInitialSettings)
            {
                Debug.WriteLine("[Settings] Skipping save during initialization");
                return;
            }

            try
            {
                if (_channelControls.Count == 0)
                {
                    Debug.WriteLine("[Settings] Skipping save - no channel controls");
                    return;
                }

                var settings = _settingsManager.CreateSettingsFromUI(
                    _serialManager.CurrentPort,
                    _channelControls.Select(c => c.AudioTargets ?? new List<AudioTarget>()).ToList(),
                    isDarkTheme,
                    InvertSliderCheckBox.IsChecked ?? false,
                    ShowSlidersCheckBox.IsChecked ?? true,
                    StartOnBootCheckBox.IsChecked ?? false,
                    StartMinimizedCheckBox.IsChecked ?? false,
                    DisableSmoothingCheckBox.IsChecked ?? false
                );

                _settingsManager.SaveSettings(settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
            }
        }

        private void SerialReconnectTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing || !_serialManager.ShouldAttemptReconnect())
            {
                Debug.WriteLine("[SerialReconnect] Stopping reconnect - closing or manual disconnect");
                _timerCoordinator.StopSerialReconnect();
                return;
            }

            Debug.WriteLine("[SerialReconnect] Attempting to reconnect...");

            // Update UI to show attempting reconnection
            Dispatcher.Invoke(() =>
            {
                ConnectionStatus.Text = "Attempting to reconnect...";
                ConnectionStatus.Foreground = Brushes.Orange;
            });

            if (_serialManager.TryConnectToSavedPort(_settingsManager.Settings.PortName))
            {
                Debug.WriteLine("[SerialReconnect] Successfully reconnected to saved port");
                return; // The Connected event will stop the timer
            }

            // Try any available port if saved port doesn't work
            try
            {
                var availablePorts = SerialPort.GetPortNames();

                if (availablePorts.Length == 0)
                {
                    Debug.WriteLine("[SerialReconnect] No serial ports available");
                    Dispatcher.Invoke(() =>
                    {
                        ConnectionStatus.Text = "Waiting for device...";
                        ConnectionStatus.Foreground = Brushes.Orange;
                        LoadAvailablePorts();
                    });
                    return;
                }

                string portToTry = availablePorts[0];
                Debug.WriteLine($"[SerialReconnect] Trying first available port: {portToTry}");

                _serialManager.InitSerial(portToTry, 9600);

                if (_serialManager.IsConnected)
                {
                    Debug.WriteLine($"[SerialReconnect] Successfully connected to {portToTry}");
                    // The Connected event will handle UI updates and stop the timer
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialReconnect] Failed to reconnect: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    ConnectionStatus.Text = "Reconnection failed - retrying...";
                    ConnectionStatus.Foreground = Brushes.Red;
                });
            }
        }

        private void SerialWatchdogTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing)
                return;

            _serialManager.CheckConnection();
        }

        private void SessionCacheTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing) return;

            try
            {
                UpdateSessionCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Session cache update: {ex.Message}");
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

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;
            settingsWindow.Show();
        }

        private void SetupAutomaticSerialConnection()
        {
            var connectionAttempts = 0;
            const int maxAttempts = 5;
            var attemptTimer = new DispatcherTimer();

            attemptTimer.Tick += (s, e) =>
            {
                connectionAttempts++;
                Debug.WriteLine($"[AutoConnect] Attempt #{connectionAttempts}");

                if (_serialManager.TryConnectToSavedPort(_settingsManager.Settings.PortName))
                {
                    Debug.WriteLine("[AutoConnect] Successfully connected!");
                    attemptTimer.Stop();
                    Dispatcher.Invoke(() => UpdateConnectionStatus());
                    return;
                }

                if (connectionAttempts >= maxAttempts)
                {
                    Debug.WriteLine($"[AutoConnect] Failed after {maxAttempts} attempts - starting auto-reconnect timer");
                    attemptTimer.Stop();

                    // Start the auto-reconnect timer since initial attempts failed
                    if (_serialManager.ShouldAttemptReconnect())
                    {
                        _timerCoordinator.StartSerialReconnect();
                    }

                    Dispatcher.Invoke(() => UpdateConnectionStatus());
                    return;
                }

                // Increase interval for subsequent attempts
                attemptTimer.Interval = TimeSpan.FromSeconds(Math.Min(2 * connectionAttempts, 10));
            };

            // Start first attempt after 2 seconds
            attemptTimer.Interval = TimeSpan.FromSeconds(2);
            attemptTimer.Start();
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

        private void StartMinimizedCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settingsManager.Settings.StartMinimized = true;
            SaveSettings();
        }

        private void StartMinimizedCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settingsManager.Settings.StartMinimized = false;
            SaveSettings();
        }

        private void StartOnBootCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            EnableStartup();
            _settingsManager.Settings.StartOnBoot = true;
            SaveSettings();
        }

        private void StartOnBootCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            DisableStartup();
            _settingsManager.Settings.StartOnBoot = false;
            SaveSettings();
        }

        private void StartSessionCacheUpdater()
        {
            _timerCoordinator.StartSessionCache();
        }

        private void SyncMuteStates()
        {
            if (!_allowVolumeApplication)
            {
                Debug.WriteLine("[Sync] Skipping SyncMuteStates - volume application disabled");
                return;
            }

            try
            {
                var audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = audioDevice.AudioSessionManager.Sessions;

                // Clean up existing handlers
                var handlersToRemove = new List<string>(_registeredHandlers.Keys);
                foreach (var target in handlersToRemove)
                {
                    try
                    {
                        _registeredHandlers.Remove(target);
                        Debug.WriteLine($"[Sync] Unregistered handler for {target}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] Failed to unregister handler for {target}: {ex.Message}");
                    }
                }
                _registeredHandlers.Clear();

                var allMappedApps = GetAllMappedApplications();
                var processDict = new Dictionary<int, string>();
                var sessionsByProcess = new Dictionary<string, AudioSessionControl>(StringComparer.OrdinalIgnoreCase);
                var sessionProcessIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Build session map
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        int pid = (int)s.GetProcessID;
                        string procName;

                        if (!processDict.TryGetValue(pid, out procName))
                        {
                            procName = AudioUtilities.GetProcessNameSafely(pid);
                            processDict[pid] = procName;
                        }

                        if (!string.IsNullOrEmpty(procName))
                        {
                            sessionsByProcess[procName] = s;
                            sessionProcessIds[procName] = pid;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] Mapping session in SyncMuteStates: {ex.Message}");
                    }
                }

                // Get all unique targets and register handlers
                var allTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ctrl in _channelControls)
                {
                    foreach (var target in ctrl.AudioTargets)
                    {
                        if (!target.IsInputDevice && !string.IsNullOrWhiteSpace(target.Name))
                        {
                            allTargets.Add(target.Name.ToLowerInvariant());
                        }
                    }
                }

                // Register handlers for each unique target
                foreach (var targetName in allTargets)
                {
                    if (targetName == "system" || targetName == "unmapped") continue;

                    if (sessionsByProcess.TryGetValue(targetName, out var matchedSession))
                    {
                        try
                        {
                            var handler = new DecoupledAudioSessionEventsHandler(this, targetName);
                            matchedSession.RegisterEventClient(handler);
                            _registeredHandlers[targetName] = handler;
                            Debug.WriteLine($"[Event] Registered DECOUPLED handler for {targetName} (PID: {sessionProcessIds[targetName]})");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ERROR] Registering handler for {targetName}: {ex.Message}");
                        }
                    }
                }

                // Apply mute states to all controls
                foreach (var ctrl in _channelControls)
                {
                    var targets = ctrl.AudioTargets;
                    foreach (var target in targets)
                    {
                        if (target.IsInputDevice) continue;

                        if (target.IsOutputDevice)
                        {
                            var device = _deviceManager.GetOutputDevice(target.Name);
                            if (device != null)
                            {
                                bool isMuted = device.AudioEndpointVolume.Mute;
                                ctrl.SetMuted(isMuted);
                            }
                            continue;
                        }

                        string targetName = target.Name?.Trim().ToLower();
                        if (string.IsNullOrEmpty(targetName)) continue;

                        if (targetName == "system")
                        {
                            bool isMuted = audioDevice.AudioEndpointVolume.Mute;
                            ctrl.SetMuted(isMuted);
                        }
                        else if (targetName == "unmapped")
                        {
                            var mappedAppsForUnmapped = new HashSet<string>(allMappedApps);
                            mappedAppsForUnmapped.Remove("unmapped");
                            _audioService.ApplyMuteStateToUnmappedApplications(ctrl.IsMuted, mappedAppsForUnmapped);
                        }
                        else
                        {
                            if (sessionsByProcess.TryGetValue(targetName, out var matchedSession))
                            {
                                try
                                {
                                    bool isMuted = matchedSession.SimpleAudioVolume.Mute;
                                    ctrl.SetMuted(isMuted);
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[ERROR] Getting mute state for {targetName}: {ex.Message}");
                                }
                            }
                        }
                    }
                }

                _hasSyncedMuteStates = true;
                Debug.WriteLine($"[Sync] Registered {_registeredHandlers.Count} decoupled handlers for unique targets");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] In SyncMuteStates: {ex.Message}");
            }
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            isDarkTheme = !isDarkTheme;
            string theme = isDarkTheme ? "Dark" : "Light";
            ApplyTheme(theme);

            // Force immediate UI refresh
            Dispatcher.Invoke(() =>
            {
                foreach (var control in _channelControls)
                {
                    control.TargetTextBox.ClearValue(TextBox.ForegroundProperty);
                    control.InvalidateVisual();
                }

                this.InvalidateVisual();
                this.UpdateLayout();
            }, DispatcherPriority.Render);

            SaveSettings();
        }

        private void UpdateConnectionStatus()
        {
            string statusText;
            Brush statusColor;

            if (_serialManager.IsConnected)
            {
                statusText = $"Connected to {_serialManager.CurrentPort}";
                statusColor = Brushes.Green;
            }
            else if (_serialManager.ShouldAttemptReconnect())
            {
                statusText = "Disconnected - Reconnecting...";
                statusColor = Brushes.Orange;
            }
            else
            {
                statusText = "Disconnected";
                statusColor = Brushes.Red;
            }

            // Ensure we're on the UI thread
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateConnectionStatus());
                return;
            }

            ConnectionStatus.Text = statusText;
            ConnectionStatus.Foreground = statusColor;

            ConnectButton.IsEnabled = true;
            ConnectButton.Content = _serialManager.IsConnected ? "Disconnect" : "Connect";

            Debug.WriteLine($"[Status] {statusText}");
        }

        private void UpdateMeters(object? sender, EventArgs e)
        {
            if (!_metersEnabled || _isClosing) return;

            const float visualGain = 1.5f;
            const float systemCalibrationFactor = 2.0f;

            try
            {
                // Cache audio device but refresh more often for responsiveness
                if (_cachedAudioDevice == null ||
                    (DateTime.Now - _lastDeviceCacheTime) > TimeSpan.FromSeconds(2))
                {
                    _cachedAudioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _lastDeviceCacheTime = DateTime.Now;
                }

                // Get sessions very frequently for real-time response
                SessionCollection sessions = null;
                if ((DateTime.Now - _lastMeterSessionRefresh).TotalMilliseconds > 50)
                {
                    sessions = _cachedAudioDevice.AudioSessionManager.Sessions;
                    _cachedSessionsForMeters = sessions;
                    _lastMeterSessionRefresh = DateTime.Now;
                }
                else
                {
                    sessions = _cachedSessionsForMeters;
                }

                if (sessions == null) return;

                foreach (var ctrl in _channelControls)
                {
                    try
                    {
                        var targets = ctrl.AudioTargets;
                        if (targets.Count == 0)
                        {
                            ctrl.UpdateAudioMeter(0);
                            continue;
                        }

                        float highestPeak = 0;
                        bool allMuted = true;

                        foreach (var target in targets)
                        {
                            try
                            {
                                if (target.IsInputDevice)
                                {
                                    var device = _deviceManager.GetInputDevice(target.Name);
                                    if (device != null)
                                    {
                                        try
                                        {
                                            float peak = device.AudioMeterInformation.MasterPeakValue;
                                            if (peak > highestPeak) highestPeak = peak;
                                            if (!device.AudioEndpointVolume.Mute) allMuted = false;
                                        }
                                        catch (ArgumentException) { }
                                    }
                                }
                                else if (target.IsOutputDevice)
                                {
                                    var device = _deviceManager.GetOutputDevice(target.Name);
                                    if (device != null)
                                    {
                                        try
                                        {
                                            float peak = device.AudioMeterInformation.MasterPeakValue;
                                            if (peak > highestPeak) highestPeak = peak;
                                            if (!device.AudioEndpointVolume.Mute) allMuted = false;
                                        }
                                        catch (ArgumentException) { }
                                    }
                                }
                                else if (string.Equals(target.Name, "system", StringComparison.OrdinalIgnoreCase))
                                {
                                    float peak = _cachedAudioDevice.AudioMeterInformation.MasterPeakValue;
                                    float systemVol = _cachedAudioDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                                    peak *= systemVol * systemCalibrationFactor;

                                    if (peak > highestPeak) highestPeak = peak;
                                    if (!_cachedAudioDevice.AudioEndpointVolume.Mute) allMuted = false;
                                }
                                else if (string.Equals(target.Name, "unmapped", StringComparison.OrdinalIgnoreCase))
                                {
                                    var mappedApps = GetAllMappedApplications();
                                    mappedApps.Remove("unmapped");

                                    float unmappedPeak = GetUnmappedApplicationsPeakLevelOptimized(mappedApps, sessions);
                                    if (unmappedPeak > highestPeak)
                                    {
                                        highestPeak = unmappedPeak;
                                    }
                                    if (!ctrl.IsMuted) allMuted = false;
                                }
                                else
                                {
                                    AudioSessionControl? matchingSession = FindSessionOptimized(sessions, target.Name.ToLowerInvariant());

                                    if (matchingSession != null)
                                    {
                                        try
                                        {
                                            float peak = matchingSession.AudioMeterInformation.MasterPeakValue;
                                            if (peak > highestPeak)
                                            {
                                                highestPeak = peak;
                                            }
                                            if (!matchingSession.SimpleAudioVolume.Mute) allMuted = false;
                                        }
                                        catch (ArgumentException) { }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ERROR] Processing target {target.Name}: {ex.Message}");
                            }
                        }

                        float finalLevel = ctrl.IsMuted || allMuted ? 0 : Math.Min(highestPeak * visualGain, 1.0f);
                        ctrl.UpdateAudioMeter(finalLevel);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] Control meter update: {ex.Message}");
                        ctrl.UpdateAudioMeter(0);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] UpdateMeters: {ex.Message}");
            }
        }

        private void UpdateSessionCache()
        {
            var defaultDevice = new MMDeviceEnumerator()
                .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _audioDevice = defaultDevice;

            var sessions = defaultDevice.AudioSessionManager.Sessions;
            int batchSize = Math.Min(sessions.Count, 15);

            for (int i = 0; i < batchSize; i++)
            {
                var session = sessions[i];
                try
                {
                    if (session == null) continue;

                    int processId = (int)session.GetProcessID;
                    string processName = AudioUtilities.GetProcessNameSafely(processId);

                    if (!string.IsNullOrEmpty(processName))
                    {
                        UpdateSessionCacheEntry(session, processName, processId);
                    }
                }
                catch (ArgumentException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Processing session in cache updater: {ex.Message}");
                }
            }
        }

        private void UpdateSessionCacheEntry(AudioSessionControl session, string processName, int processId)
        {
            try
            {
                string sessionId = session.GetSessionIdentifier?.ToLowerInvariant() ?? "";
                string instanceId = session.GetSessionInstanceIdentifier?.ToLowerInvariant() ?? "";

                bool found = false;
                for (int i = 0; i < _sessionIdCache.Count; i++)
                {
                    if (_sessionIdCache[i].sessionId == sessionId && _sessionIdCache[i].instanceId == instanceId)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    _sessionIdCache.Add((session, sessionId, instanceId));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Updating session cache entry: {ex.Message}");
            }
        }

        private AudioSessionControl? FindSessionOptimized(SessionCollection sessions, string targetName)
        {
            try
            {
                int maxSessions = Math.Min(sessions.Count, 20);

                for (int i = 0; i < maxSessions; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int pid = (int)session.GetProcessID;
                        if (pid <= 4) continue;

                        string processName = AudioUtilities.GetProcessNameSafely(pid);

                        if (processName == targetName) return session;
                    }
                    catch (ArgumentException) { continue; }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Finding session optimized: {ex.Message}");
            }

            return null;
        }

        private float GetUnmappedApplicationsPeakLevelOptimized(HashSet<string> mappedApplications, SessionCollection sessions)
        {
            float highestPeak = 0;

            try
            {
                int maxSessions = Math.Min(sessions.Count, 15);

                for (int i = 0; i < maxSessions; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int pid = (int)session.GetProcessID;
                        if (pid <= 4) continue;

                        string processName = AudioUtilities.GetProcessNameSafely(pid);

                        if (string.IsNullOrEmpty(processName) || mappedApplications.Contains(processName))
                        {
                            continue;
                        }

                        try
                        {
                            float peak = session.AudioMeterInformation.MasterPeakValue;
                            if (peak > 0.01f && peak > highestPeak)
                            {
                                highestPeak = peak;
                            }
                        }
                        catch { continue; }
                    }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Getting unmapped peak levels optimized: {ex.Message}");
            }

            return highestPeak;
        }

        private bool IsStartupEnabled()
        {
            const string appName = "DeejNG";
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            var value = key?.GetValue(appName) as string;
            return !string.IsNullOrEmpty(value);
        }

        private void InvertSlider_Checked(object sender, RoutedEventArgs e)
        {
            _settingsManager.SaveInvertState(true);
        }

        private void InvertSlider_Unchecked(object sender, RoutedEventArgs e)
        {
            _settingsManager.SaveInvertState(false);
        }

        private void MyNotifyIcon_Click(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.InvalidateMeasure();
                this.UpdateLayout();
            }
            else
            {
                this.WindowState = WindowState.Minimized;
                this.Hide();
            }
        }

        #endregion Private Methods

        #region Public Classes

        public class DecoupledAudioSessionEventsHandler : IAudioSessionEventsHandler
        {
            private readonly MainWindow _mainWindow;
            private readonly string _targetName;

            public DecoupledAudioSessionEventsHandler(MainWindow mainWindow, string targetName)
            {
                _mainWindow = mainWindow;
                _targetName = targetName;
            }

            public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }
            public void OnDisplayNameChanged(string displayName) { }
            public void OnGroupingParamChanged(ref Guid groupingId) { }
            public void OnIconPathChanged(string iconPath) { }

            public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    Debug.WriteLine($"[DecoupledHandler] Session disconnected for {_targetName}: {disconnectReason}");
                    _mainWindow.HandleSessionDisconnected(_targetName);
                });
            }

            public void OnSimpleVolumeChanged(float volume, bool mute)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var control = _mainWindow.FindControlForTarget(_targetName);
                    control?.SetMuted(mute);
                });
            }

            public void OnStateChanged(AudioSessionState state)
            {
                if (state == AudioSessionState.AudioSessionStateExpired)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        var control = _mainWindow.FindControlForTarget(_targetName);
                        control?.HandleSessionExpired();
                    });
                }
            }

            public void OnVolumeChanged(float volume, bool mute)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var control = _mainWindow.FindControlForTarget(_targetName);
                    control?.SetMuted(mute);
                });
            }
        }

        #endregion Public Classes
    }

    internal static class IconHandler
    {
        private static string IconPath => Path.Combine(AppContext.BaseDirectory, "icon.ico");

        public static void AddIconToRemovePrograms(string productName)
        {
            try
            {
                if (File.Exists(IconPath))
                {
                    var uninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                    if (uninstallKey != null)
                    {
                        foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                        {
                            using (var subKey = uninstallKey.OpenSubKey(subKeyName, writable: true))
                            {
                                if (subKey == null) continue;

                                var displayName = subKey.GetValue("DisplayName") as string;
                                if (string.Equals(displayName, productName, StringComparison.OrdinalIgnoreCase))
                                {
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
                Console.WriteLine($"Error setting uninstall icon: {ex.Message}");
            }
        }
    }
}