using DeejNG.Classes;
using DeejNG.Core.Configuration;
using DeejNG.Core.Interfaces;
using DeejNG.Core.Services;
using DeejNG.Dialogs;
using DeejNG.Infrastructure.System;
using DeejNG.Models;
using DeejNG.Services;
using DeejNG.Views;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        #region Public Fields

        public List<ChannelControl> _channelControls = new();

        #endregion Public Fields

        #region Private Fields

        private const float INLINE_MUTE_TRIGGER = 9999f;

        // Object pools to reduce allocations (UNUSED - can be removed)
        private readonly Queue<List<AudioTarget>> _audioTargetListPool = new();

        private readonly HashSet<string> _cachedMappedApplications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly MMDeviceEnumerator _deviceEnumerator = new();

        // New Manager Fields
        private readonly DeviceCacheManager _deviceManager;

        // Inline mute support (triggered by 9999 value from hardware)
        private readonly HashSet<int> _inlineMutedChannels = new HashSet<int>();

        private readonly IOverlayService _overlayService;

        private readonly IPowerManagementService _powerManagementService;

        private readonly ProfileManager _profileManager;

        private readonly SerialConnectionManager _serialManager;

        private readonly AppSettingsManager _settingsManager;

        private readonly ISystemIntegrationService _systemIntegrationService;

        private readonly HashSet<string> _tempStringSet = new(StringComparer.OrdinalIgnoreCase);

        // Cached collections to avoid repeated allocations
        private readonly List<AudioTarget> _tempTargetList = new();

        private readonly TimerCoordinator _timerCoordinator;

        private readonly object _unmappedLock = new object();

        private readonly TimeSpan UNMAPPED_THROTTLE_INTERVAL = TimeSpan.FromMilliseconds(100);

        // 500ms calibration period
        private bool _allowVolumeApplication = false;

        private MMDevice _audioDevice;

        private AudioService _audioService;

        private ButtonActionHandler _buttonActionHandler;

        private ObservableCollection<ButtonIndicatorViewModel> _buttonIndicators = new();

        private MMDevice _cachedAudioDevice;

        private SessionCollection _cachedSessionsForMeters;

        private bool _disableSmoothing = false;

        private int _expectedSliderCount = -1;

        private float _exponentialVolumeFactor = 2f;

        private bool _hasLoadedInitialSettings = false;

        private bool _hasSyncedMuteStates = false;

        private bool _isClosing = false;

        private bool _isExiting = false;

        private bool _isInitializing = true;

        // Track if we're actually exiting vs minimizing
        private DateTime _lastDeviceCacheTime = DateTime.MinValue;

        private DateTime _lastForcedCleanup = DateTime.MinValue;

        private DateTime _lastMeterSessionRefresh = DateTime.MinValue;

        private DateTime _lastUnmappedMeterUpdate = DateTime.MinValue;

        private bool _metersEnabled = true;

        // Track toggle state for latching buttons (PlayPause)
        private bool _playPauseState = false;

        private Dictionary<string, IAudioSessionEventsHandler> _registeredHandlers = new();

        private int _sessionCacheHitCount = 0;

        private List<(AudioSessionControl session, string sessionId, string instanceId)> _sessionIdCache = new();

        private AudioEndpointVolume _systemVolume;

        private bool _useExponentialVolume = false;

        private bool isDarkTheme = false;

        #endregion Private Fields

        #region Public Constructors

        // false = paused/stopped, true = playing
        public MainWindow()
        {
            _isInitializing = true;
            // Use default render mode (hardware accelerated when available) for smoother UI
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");

            // Initialize managers
            _deviceManager = new DeviceCacheManager();
            _settingsManager = new AppSettingsManager();
            _profileManager = new ProfileManager(_settingsManager);
            _serialManager = new SerialConnectionManager();
            _timerCoordinator = new TimerCoordinator();
            _overlayService = ServiceLocator.Get<IOverlayService>();
            _systemIntegrationService = ServiceLocator.Get<ISystemIntegrationService>();
            _powerManagementService = ServiceLocator.Get<IPowerManagementService>();

            // Wire up power management events
            _powerManagementService.SystemSuspending += OnSystemSuspending;
            _powerManagementService.SystemResuming += OnSystemResuming;
            _powerManagementService.SystemResumed += OnSystemResumed;

            _audioService = new AudioService();

            // Load profiles (includes migration if needed)
            _profileManager.LoadProfiles();

            // Load settings from active profile but don't auto-connect to serial port yet
            LoadSettingsWithoutSerialConnection();

            // Initialize profile UI
            LoadProfilesIntoUI();

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
            _serialManager.ButtonStateChanged += HandleButtonPress;
            _serialManager.Connected += () =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateConnectionStatus();

                    // Update ComboBox to show the connected port
                    if (!string.IsNullOrEmpty(_serialManager.CurrentPort))
                    {
                        ComPortSelector.SelectedItem = _serialManager.CurrentPort;
                    }

                    // Stop reconnection timer on successful connection
                    _timerCoordinator.StopSerialReconnect();
                }, DispatcherPriority.Background); // FIX: Use background priority to prevent focus stealing
            };
            _serialManager.Disconnected += () =>
            {
                _allowVolumeApplication = false;
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateConnectionStatus();

                    // Start reconnection timer only if not manually disconnected
                    if (_serialManager.ShouldAttemptReconnect())
                    {
                        _timerCoordinator.StartSerialReconnect();
                    }
                }, DispatcherPriority.Background); // FIX: Use background priority to prevent focus stealing
            };
            _serialManager.ProtocolValidated += (validatedPort) =>
            {
                Dispatcher.BeginInvoke(() =>
                {

                    // Only save the port name once protocol is validated
                    _settingsManager.AppSettings.PortName = validatedPort;
                    SaveSettings();

                    UpdateConnectionStatus();
                }, DispatcherPriority.Background);
            };

            StartSessionCacheUpdater();

            // Start timers
            _timerCoordinator.StartSerialWatchdog();

            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            IconHandler.AddIconToRemovePrograms("DeejNG");
            _systemIntegrationService.SetDisplayIcon();

            // CRITICAL: Initialize overlay and subscribe to events BEFORE potentially hiding the window
            // This ensures the subscription happens even when starting minimized


            _overlayService.Initialize();

            // Subscribe to PositionChanged event
            _overlayService.PositionChanged += OnOverlayPositionChanged;


            // Update overlay with settings
            _overlayService.UpdateSettings(_settingsManager.AppSettings);

            _isInitializing = false;
            if (_settingsManager.AppSettings.StartMinimized)
            {
                WindowState = WindowState.Minimized;
                Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }

            _timerCoordinator.StartForceCleanup();

            // Setup automatic serial connection
            SetupAutomaticSerialConnection();

            // Initialize theme selector
            InitializeThemeSelector();

            // Set version text from ClickOnce manifest or assembly
            VersionText.Text = GetApplicationVersion();
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
                    if (name.Equals("current", StringComparison.OrdinalIgnoreCase))
                    {
                        var label = "Current";
                        var focusTarget = AudioUtilities.GetCurrentFocusTarget();
                        if (focusTarget != "")
                        {
                            label += $" ({char.ToUpper(focusTarget[0]) + focusTarget[1..].ToLower()})";
                        }

                        return label;
                    }

                    // For regular app names, return the full name (wrapping will handle display)
                    return char.ToUpper(name[0]) + name.Substring(1).ToLower();
                }
            }

            // Fallback to channel number
            int channelIndex = _channelControls.IndexOf(control);
            return $"Channel {channelIndex + 1}";
        }

        public AppSettings GetCurrentSettings()
        {
            return _settingsManager?.AppSettings ?? new AppSettings();
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

                }
            }
            catch (Exception ex)
            {

            }
        }

        public void SaveOverlayPosition(double x, double y)
        {


            // Update settings with position AND screen information
            if (_settingsManager.AppSettings != null)
            {
                _settingsManager.AppSettings.OverlayX = x;
                _settingsManager.AppSettings.OverlayY = y;

                // Save screen information for multi-monitor support
                var screenInfo = DeejNG.Core.Helpers.ScreenPositionManager.GetScreenInfo(x, y);
                _settingsManager.AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                _settingsManager.AppSettings.OverlayScreenBounds = screenInfo.Bounds;



                // Save directly to settings.json - no debouncing needed since this only fires on mouse release
                _settingsManager.SaveSettings(_settingsManager.AppSettings);


            }
        }

        public void ShowVolumeOverlay()
        {
            if (_settingsManager.AppSettings?.OverlayEnabled == true)
            {
                var volumes = _channelControls.Select(c => c.CurrentVolume).ToList();
                var labels = _channelControls.Select(c => GetChannelLabel(c)).ToList();
                _overlayService.ShowOverlay(volumes, labels);
            }
        }

        /// <summary>
        /// Updates COM port and automatically switches connection if needed.
        /// </summary>
        public void UpdateComPort(string newPort, int baudRate)
        {
            if (string.IsNullOrEmpty(newPort)) return;


            // Update settings immediately
            _settingsManager.AppSettings.PortName = newPort;
            _settingsManager.AppSettings.BaudRate = baudRate;
            SaveSettings();

            // Update UI selector immediately (before async operations)
            ComPortSelector.SelectedItem = newPort;

            // If connected to a different port, switch
            if (_serialManager.IsConnected &&
                !string.Equals(_serialManager.CurrentPort, newPort, StringComparison.OrdinalIgnoreCase))
            {


                // Stop reconnection timer
                _timerCoordinator.StopSerialReconnect();

                // Disconnect from old port
                _serialManager.ManualDisconnect();

                // Wait for clean disconnect, then reconnect
                var reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                EventHandler reconnectHandler = null;
                reconnectHandler = (s, args) =>
                {
                    reconnectTimer.Tick -= reconnectHandler;
                    reconnectTimer.Stop();


                    _serialManager.InitSerial(newPort, baudRate);
                };
                reconnectTimer.Tick += reconnectHandler;
                reconnectTimer.Start();
            }
            // If not connected, auto-connect if user selected a port
            else if (!_serialManager.IsConnected && !_isInitializing)
            {

                _serialManager.InitSerial(newPort, baudRate);
            }
        }

        public void UpdateOverlayPosition(double x, double y)
        {
            _overlayService.UpdatePosition(x, y);
        }

        public void UpdateOverlaySettings(AppSettings newSettings)
        {
            _overlayService.UpdateSettings(newSettings);

            // Update the settings manager with the new settings
            _settingsManager.AppSettings.OverlayEnabled = newSettings.OverlayEnabled;
            _settingsManager.AppSettings.OverlayOpacity = newSettings.OverlayOpacity;
            _settingsManager.AppSettings.OverlayTimeoutSeconds = newSettings.OverlayTimeoutSeconds;
            _settingsManager.AppSettings.OverlayTextColor = newSettings.OverlayTextColor;
            _settingsManager.AppSettings.OverlayX = newSettings.OverlayX;
            _settingsManager.AppSettings.OverlayY = newSettings.OverlayY;
            _settingsManager.AppSettings.OverlayScreenDevice = newSettings.OverlayScreenDevice;
            _settingsManager.AppSettings.OverlayScreenBounds = newSettings.OverlayScreenBounds;

            // Update button configuration
            _settingsManager.AppSettings.NumberOfButtons = newSettings.NumberOfButtons;
            _settingsManager.AppSettings.ButtonMappings = newSettings.ButtonMappings;

            // BUGFIX: Update baud rate from SettingsWindow
            // This ensures baud rate changes from the UI are persisted
            _settingsManager.AppSettings.BaudRate = newSettings.BaudRate;



            // Reconfigure button layout if button count changed
            ConfigureButtonLayout();

            // CRITICAL FIX: Save to profile after updating overlay settings
            SaveSettings();
        }

        #endregion Public Methods

        #region Protected Methods

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;

            // Force save settings (including overlay position) before shutdown
            // CRITICAL FIX: Save to profile, not legacy settings file
            if (_settingsManager?.AppSettings != null && _profileManager != null)
            {
                try
                {
                    // Update the active profile with current settings and save
                    _profileManager.UpdateActiveProfileSettings(_settingsManager.AppSettings);
                    _profileManager.SaveProfiles();

                }
                catch (Exception ex)
                {

                }
            }

            // Stop all timers first
            _timerCoordinator.StopAll();

            // Unsubscribe from power management events
            if (_powerManagementService != null)
            {
                _powerManagementService.SystemSuspending -= OnSystemSuspending;
                _powerManagementService.SystemResuming -= OnSystemResuming;
                _powerManagementService.SystemResumed -= OnSystemResumed;
            }

            // Dispose services
            _overlayService?.Dispose();
            _powerManagementService?.Dispose();

            // Clean up all registered event handlers
            foreach (var target in _registeredHandlers.Keys.ToList())
            {
                try
                {
                    _registeredHandlers.Remove(target);

                }
                catch (Exception ex)
                {

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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
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

            }
        }

        private void ApplyVolumeToTargets(ChannelControl ctrl, List<AudioTarget> targets, float level)
        {
            if (!_allowVolumeApplication) return;

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
                        lock (_unmappedLock)
                        {
                            var now = DateTime.Now;
                            if ((now - _lastUnmappedMeterUpdate) < UNMAPPED_THROTTLE_INTERVAL)
                            {
                                // Skip for responsiveness
                            }
                            _lastUnmappedMeterUpdate = now;

                            var mappedApps = GetAllMappedApplications();
                            mappedApps.Remove("unmapped");
                            if (mappedApps.Remove("current"))
                            {
                                var focusTarget = AudioUtilities.GetCurrentFocusTarget();
                                if (focusTarget != "")
                                {
                                    mappedApps.Add(focusTarget);
                                }
                            }
                            _audioService.ApplyVolumeToUnmappedApplications(level, ctrl.IsMuted, mappedApps);
                        }
                    }
                    else if (string.Equals(target.Name, "current", StringComparison.OrdinalIgnoreCase))
                    {
                        _audioService.ApplyVolumeToCurrent(level, ctrl.IsMuted);
                    }
                    else
                    {
                        _audioService.ApplyVolumeToTarget(target.Name, level, ctrl.IsMuted);
                    }
                }
                catch (Exception ex)
                {

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
                    systemControl.SetMuted(data.Muted, applyToAudio: false);
                }
            });
        }

        private void AutoSizeToChannels()
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var work = SystemParameters.WorkArea;

                    // Measure desired size of the channel area (independent of current window size)
                    SliderPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Size desiredChannels = SliderPanel.DesiredSize;

                    // Compute target width based on desired channel width
                    double width = Math.Min(Math.Max(desiredChannels.Width + 60, 900), work.Width - 40);
                    if (Math.Abs(this.Width - width) > 2)
                        this.Width = width;

                    // Compute target height using measured channel height + chrome rows
                    double titleH = TitleBarGrid?.ActualHeight > 0 ? TitleBarGrid.ActualHeight : 48;
                    // Toolbar height is auto-calculated (profile/settings row)
                    double toolbarH = 44; // Approximate toolbar height
                    double statusH = StatusBar?.ActualHeight ?? 0;
                    double contentH = Math.Max(desiredChannels.Height + 40, 320); // pad, minimum
                    double desired = titleH + toolbarH + contentH + statusH + 60; // overall padding
                    double height = Math.Min(desired, work.Height - 20);
                    if (Math.Abs(this.Height - height) > 2)
                        this.Height = height;
                }, DispatcherPriority.Render);
            }
            catch { }
        }

        /// <summary>
        /// Handles UI button indicator clicks to execute button actions.
        /// </summary>
        private void ButtonIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is Border border && border.DataContext is ButtonIndicatorViewModel viewModel)
                {
                    var settings = _settingsManager.AppSettings;
                    if (settings == null || settings.ButtonMappings == null) return;

                    // Find the mapping for this button
                    var mapping = settings.ButtonMappings.FirstOrDefault(m => m.ButtonIndex == viewModel.ButtonIndex);
                    if (mapping == null || mapping.Action == ButtonAction.None) return;

                    // Ensure button handler is initialized
                    if (_buttonActionHandler == null)
                    {
                        _buttonActionHandler = new ButtonActionHandler(_channelControls);
                    }

                    // Determine button type
                    bool isMuteAction = mapping.Action == ButtonAction.MuteChannel ||
                                       mapping.Action == ButtonAction.GlobalMute;
                    bool isPlayPauseAction = mapping.Action == ButtonAction.MediaPlayPause;
                    bool isMomentaryAction = mapping.Action == ButtonAction.MediaNext ||
                                            mapping.Action == ButtonAction.MediaPrevious ||
                                            mapping.Action == ButtonAction.MediaStop;

                    // For momentary buttons, briefly show pressed state
                    if (isMomentaryAction)
                    {
                        viewModel.IsPressed = true;

                        // Reset after a brief delay
                        Task.Delay(150).ContinueWith(_ =>
                        {
                            Dispatcher.BeginInvoke(() =>
                            {
                                viewModel.IsPressed = false;
                            });
                        });
                    }



                    // Execute the action
                    _buttonActionHandler.ExecuteAction(mapping);

                    // Update indicator for latched buttons
                    if (isMuteAction)
                    {
                        bool muteState = false;

                        if (mapping.Action == ButtonAction.MuteChannel &&
                            mapping.TargetChannelIndex >= 0 &&
                            mapping.TargetChannelIndex < _channelControls.Count)
                        {
                            muteState = _channelControls[mapping.TargetChannelIndex].IsMuted;
                        }
                        else if (mapping.Action == ButtonAction.GlobalMute)
                        {
                            muteState = _channelControls.Any(c => c.IsMuted);
                        }

                        viewModel.IsPressed = muteState;
                    }
                    else if (isPlayPauseAction)
                    {
                        _playPauseState = !_playPauseState;
                        viewModel.IsPressed = _playPauseState;
                    }
                }
            }
            catch (Exception ex)
            {

            }
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

                }
            }
            catch (Exception ex)
            {

            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void ComPortSelector_DropDownOpened(object sender, EventArgs e)
        {

            LoadAvailablePorts();
        }

        private void ComPortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComPortSelector.SelectedItem is string selectedPort)
            {
                _serialManager.SetUserSelectedPort(selectedPort);

                // Save the port immediately when user manually selects it
                // This ensures the selection persists across reboots even if connection fails
                if (!_isInitializing && _hasLoadedInitialSettings)
                {

                    _settingsManager.AppSettings.PortName = selectedPort;
                    SaveSettings();
                }

                // AUTO-CONNECT: Handle port switching (even during initialization)
                // Skip only if we're in the very first load and no user interaction
                if (_hasLoadedInitialSettings)
                {
                    bool isCurrentlyConnected = _serialManager.IsConnected;
                    string currentPort = _serialManager.CurrentPort;



                    // If connected to a different port, disconnect and reconnect
                    if (isCurrentlyConnected && !string.IsNullOrEmpty(currentPort) &&
                        !string.Equals(currentPort, selectedPort, StringComparison.OrdinalIgnoreCase))
                    {

                        // Stop reconnection timer
                        _timerCoordinator.StopSerialReconnect();

                        // Disconnect from old port
                        _serialManager.ManualDisconnect();

                        // Use a short delay to ensure clean disconnect before reconnecting
                        var reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                        EventHandler reconnectHandler = null;
                        reconnectHandler = (s, args) =>
                        {
                            reconnectTimer.Tick -= reconnectHandler;
                            reconnectTimer.Stop();

                            // Connect to new port with saved baud rate
                            int baud = _settingsManager.AppSettings.BaudRate > 0
                                ? _settingsManager.AppSettings.BaudRate
                                : 9600;


                            _serialManager.InitSerial(selectedPort, baud);
                        };
                        reconnectTimer.Tick += reconnectHandler;
                        reconnectTimer.Start();
                    }
                    // If not connected at all, auto-connect
                    else if (!isCurrentlyConnected && !_isInitializing)
                    {

                        int baud = _settingsManager.AppSettings.BaudRate > 0
                            ? _settingsManager.AppSettings.BaudRate
                            : 9600;

                        _serialManager.InitSerial(selectedPort, baud);
                    }
                }
            }
        }

        /// <summary>
        /// Configures button indicators UI. Buttons are now auto-detected from serial data.
        /// </summary>
        private void ConfigureButtonLayout()
        {
            try
            {
                // Buttons are now auto-detected from serial protocol (10000/10001 values)
                // No need to configure serial manager - it auto-detects

                // Initialize button handler lazily if not yet created
                if (_buttonActionHandler == null)
                {
                    _buttonActionHandler = new ButtonActionHandler(_channelControls);
                }

                // Update button indicators UI
                UpdateButtonIndicators();


            }
            catch (Exception ex)
            {

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


                    // Use the last saved baud rate if available, otherwise default to 9600
                    int baud = _settingsManager.AppSettings.BaudRate > 0
                        ? _settingsManager.AppSettings.BaudRate
                        : 9600;

                    // Update button state immediately
                    ConnectButton.IsEnabled = false;
                    ConnectButton.Content = "Connecting...";

                    // Stop automatic reconnection while user is manually connecting
                    _timerCoordinator.StopSerialReconnect();

                    // Try connection
                    _serialManager.InitSerial(selectedPort, baud);

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

            }
        }

        /// <summary>
        /// Deletes the current profile
        /// </summary>
        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string currentName = _profileManager.ActiveProfile.Name;

                // Prevent deletion of last profile
                if (_profileManager.ProfileCollection.Profiles.Count <= 1)
                {
                    ConfirmationDialog.ShowOK("Cannot Delete",
                        "Cannot delete the last profile. At least one profile must exist.", this);
                    return;
                }

                var result = ConfirmationDialog.ShowYesNo("Confirm Delete",
                    $"Are you sure you want to delete profile '{currentName}'?\n\n" +
                    "This action cannot be undone.", this);

                if (result == ConfirmationDialog.ButtonResult.Yes)
                {
                    if (_profileManager.DeleteProfile(currentName))
                    {
                        LoadProfilesIntoUI();

                        // Load the new active profile
                        LoadSettingsWithoutSerialConnection();

                        ConfirmationDialog.ShowOK("Success",
                            $"Profile '{currentName}' deleted successfully.", this);
                    }
                    else
                    {
                        ConfirmationDialog.ShowOK("Error",
                            $"Failed to delete profile '{currentName}'.", this);
                    }
                }
            }
            catch (Exception ex)
            {
                ConfirmationDialog.ShowOK("Error",
                    $"Error deleting profile: {ex.Message}", this);
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

        private async void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _isExiting = true; // Set flag to allow actual exit
            Application.Current.Shutdown();
        }

        private void ExponentialVolumeFactorSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _exponentialVolumeFactor = (float)e.NewValue;
            SaveSettings();
        }

        private AudioSessionControl? FindSessionOptimized(SessionCollection sessions, string targetName)
        {
            try
            {
                int maxSessions = Math.Min(sessions.Count, 20);

                // Normalize target name for fuzzy matching
                string cleanedTargetName = Path.GetFileNameWithoutExtension(targetName).ToLowerInvariant();

                for (int i = 0; i < maxSessions; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int pid = (int)session.GetProcessID;
                        if (pid <= 4) continue;

                        string processName = AudioUtilities.GetProcessNameSafely(pid);
                        if (string.IsNullOrEmpty(processName)) continue;

                        // Normalize process name for fuzzy matching
                        string cleanedProcessName = Path.GetFileNameWithoutExtension(processName).ToLowerInvariant();

                        // Use fuzzy matching: exact match OR contains
                        if (cleanedProcessName.Equals(cleanedTargetName, StringComparison.OrdinalIgnoreCase) ||
                            cleanedProcessName.Contains(cleanedTargetName, StringComparison.OrdinalIgnoreCase) ||
                            cleanedTargetName.Contains(cleanedProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            return session;
                        }
                    }
                    catch (ArgumentException) { continue; }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {

            }

            return null;
        }

        private void ForceCleanupTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing) return;

            try
            {

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

            }
            catch (Exception ex)
            {

            }
        }

        private void GenerateSliders(int count)
        {
            SliderPanel.Children.Clear();
            _channelControls.Clear();

            // CRITICAL FIX: Load from active profile, not from disk
            var savedSettings = _profileManager.GetActiveProfileSettings();
            var savedTargetGroups = savedSettings?.SliderTargets ?? new List<List<AudioTarget>>();

            _isInitializing = true;
            _allowVolumeApplication = false;



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
                control.SetMuted(false, applyToAudio: false);

                control.TargetChanged += (_, _) => SaveSettings();

                control.VolumeOrMuteChanged += (targets, vol, mute) =>
                {
                    if (!_allowVolumeApplication) return;
                    ApplyVolumeToTargets(control, targets, vol);

                    // Update button indicators when mute state changes via UI
                    UpdateMuteButtonIndicators();
                };

                control.SessionDisconnected += (sender, target) =>
                {

                    if (_registeredHandlers.TryGetValue(target, out var handler))
                    {
                        _registeredHandlers.Remove(target);

                    }
                };

                _channelControls.Add(control);
                SliderPanel.Children.Add(control);
            }

            SetMeterVisibilityForAll(ShowSlidersCheckBox.IsChecked ?? true);

            AutoSizeToChannels();



            // Configure button layout after sliders are generated
            ConfigureButtonLayout();
        }

        /// <summary>
        /// Gets the application version from ClickOnce manifest or assembly
        /// </summary>
        private string GetApplicationVersion()
        {
            try
            {
                // Try to read version from ClickOnce manifest file
                string manifestPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DeejNG.exe.manifest");
                if (System.IO.File.Exists(manifestPath))
                {
                    var manifestXml = System.Xml.Linq.XDocument.Load(manifestPath);
                    var assemblyIdentity = manifestXml.Descendants().FirstOrDefault(x => x.Name.LocalName == "assemblyIdentity");
                    if (assemblyIdentity != null)
                    {
                        var versionAttr = assemblyIdentity.Attribute("version");
                        if (versionAttr != null)
                        {
                            return $"v{versionAttr.Value}";
                        }
                    }
                }
            }
            catch { }

            // Fallback to assembly version
            try
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return $"v{version?.Major}.{version?.Minor}.{version?.Build}.{version?.Revision}";
            }
            catch
            {
                return "v1.0.0";
            }
        }

        private string GetButtonActionIcon(ButtonAction action)
        {
            return action switch
            {
                ButtonAction.MediaPlayPause => "⏯",
                ButtonAction.MediaNext => "⏭",
                ButtonAction.MediaPrevious => "⏮",
                ButtonAction.MediaStop => "⏹",
                ButtonAction.MuteChannel => "🔇",
                ButtonAction.GlobalMute => "🔕",
                ButtonAction.ToggleInputOutput => "🔄",
                _ => "🔘"
            };
        }

        private string GetButtonActionText(ButtonMapping mapping)
        {
            if (mapping.Action == ButtonAction.None)
                return "Not assigned";

            string actionText = mapping.Action switch
            {
                ButtonAction.MediaPlayPause => "Play/Pause",
                ButtonAction.MediaNext => "Next Track",
                ButtonAction.MediaPrevious => "Previous Track",
                ButtonAction.MediaStop => "Stop",
                ButtonAction.MuteChannel => $"Mute Ch{mapping.TargetChannelIndex + 1}",
                ButtonAction.GlobalMute => "Global Mute",
                ButtonAction.ToggleInputOutput => "Toggle I/O",
                _ => mapping.Action.ToString()
            };

            return actionText;
        }

        private string GetButtonActionTooltip(ButtonMapping mapping)
        {
            if (mapping.Action == ButtonAction.None)
                return "No action assigned to this button";

            return $"Button {mapping.ButtonIndex + 1}: {GetButtonActionText(mapping)}";
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

            }

            return highestPeak;
        }

        /// <summary>
        /// Handles button press events from the serial manager.
        /// OPTIMIZATION: Uses single dispatcher call to reduce handle accumulation over time.
        /// </summary>
        private void HandleButtonPress(int buttonIndex, bool isPressed)
        {

            try
            {
                var settings = _settingsManager.AppSettings;
                if (settings == null || settings.ButtonMappings == null) return;

                // Find the mapping for this button
                var mapping = settings.ButtonMappings.FirstOrDefault(m => m.ButtonIndex == buttonIndex);
                if (mapping == null) return;

                // Determine button type:
                // - Mute actions: Latched (show mute state)
                // - Play/Pause: Latched (show play state toggle)
                // - Next/Prev/Stop: Momentary (show press state only)
                bool isMuteAction = mapping.Action == ButtonAction.MuteChannel ||
                                   mapping.Action == ButtonAction.GlobalMute;
                bool isPlayPauseAction = mapping.Action == ButtonAction.MediaPlayPause;
                bool isMomentaryAction = mapping.Action == ButtonAction.MediaNext ||
                                        mapping.Action == ButtonAction.MediaPrevious ||
                                        mapping.Action == ButtonAction.MediaStop;

                // Ensure button handler is initialized (do this outside dispatcher to avoid repeated checks)
                if (_buttonActionHandler == null)
                {
                    _buttonActionHandler = new ButtonActionHandler(_channelControls);
                }

                // OPTIMIZATION: Single dispatcher call handles all UI updates and action execution
                // This reduces handle accumulation from repeated BeginInvoke calls
                Dispatcher.BeginInvoke(() =>
                {
                    var indicator = _buttonIndicators.FirstOrDefault(b => b.ButtonIndex == buttonIndex);

                    // For momentary buttons: always update press state (for both press and release)
                    if (isMomentaryAction && indicator != null)
                    {
                        if (indicator.IsPressed != isPressed)
                        {
                            indicator.IsPressed = isPressed;
                        }
                    }

                    // Only process actions on button press (not release)
                    if (!isPressed) return;

                    if (mapping.Action == ButtonAction.None)
                    {

                        return;
                    }

                    // Execute the action
                    _buttonActionHandler.ExecuteAction(mapping);

                    // Update indicator based on button type
                    if (indicator != null)
                    {
                        // For mute actions, show the actual mute state
                        if (isMuteAction)
                        {
                            bool muteState = false;

                            if (mapping.Action == ButtonAction.MuteChannel &&
                                mapping.TargetChannelIndex >= 0 &&
                                mapping.TargetChannelIndex < _channelControls.Count)
                            {
                                muteState = _channelControls[mapping.TargetChannelIndex].IsMuted;
                            }
                            else if (mapping.Action == ButtonAction.GlobalMute)
                            {
                                // Global mute - check if any channel is muted
                                muteState = _channelControls.Any(c => c.IsMuted);
                            }

                            if (indicator.IsPressed != muteState)
                            {
                                indicator.IsPressed = muteState;

                            }
                        }
                        // For play/pause, toggle the state
                        else if (isPlayPauseAction)
                        {
                            _playPauseState = !_playPauseState;
                            indicator.IsPressed = _playPauseState;

                        }
                    }
                });
            }
            catch (Exception ex)
            {

            }
        }

        private void HandleSliderData(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                if (!data.Contains('|') && !float.TryParse(data, out _))
                {

                    return;
                }

                string[] parts = data.Split('|');
                if (parts.Length == 0) return;

                // REVERT: Use normal priority for responsive UI
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Hardware slider count takes priority
                        if (_channelControls.Count != parts.Length)
                        {


                            var currentTargets = _channelControls.Select(c => c.AudioTargets).ToList();
                            _expectedSliderCount = parts.Length;
                            GenerateSliders(parts.Length);
                            AutoSizeToChannels();

                            for (int i = 0; i < Math.Min(currentTargets.Count, _channelControls.Count); i++)
                            {
                                _channelControls[i].AudioTargets = currentTargets[i];
                            }

                            SaveSettings();
                            return;
                        }

                        // Process slider data
                        int maxIndex = Math.Min(parts.Length, _channelControls.Count);

                        for (int i = 0; i < maxIndex; i++)
                        {
                            if (!float.TryParse(parts[i].Trim(), out float rawValue)) continue;

                            var ctrl = _channelControls[i];
                            var targets = ctrl.AudioTargets;

                            if (targets.Count == 0) continue;

                            // Check for inline mute trigger (9999)
                            if (rawValue >= INLINE_MUTE_TRIGGER - 0.5f && rawValue <= INLINE_MUTE_TRIGGER + 0.5f)
                            {
                                // Mute this channel via inline mute
                                if (!_inlineMutedChannels.Contains(i))
                                {
                                    _inlineMutedChannels.Add(i);
                                    ctrl.SetMuted(true, applyToAudio: true);
                                    UpdateMuteButtonIndicators(); // Update button indicators

                                }
                                // Don't update slider position when muted
                                continue;
                            }
                            else
                            {
                                // Normal value range - unmute if it was inline-muted
                                if (_inlineMutedChannels.Contains(i))
                                {
                                    _inlineMutedChannels.Remove(i);
                                    ctrl.SetMuted(false, applyToAudio: true);
                                    UpdateMuteButtonIndicators(); // Update button indicators

                                }
                            }

                            // Process normal slider value
                            float level = rawValue;
                            if (level <= 10) level = 0;
                            if (level >= 1013) level = 1023;

                            level = Math.Clamp(level / 1023f, 0f, 1f);
                            if (InvertSliderCheckBox.IsChecked ?? false)
                                level = 1f - level;

                            if (_useExponentialVolume)
                                level = (MathF.Pow(_exponentialVolumeFactor, level) / (_exponentialVolumeFactor - 1)) - (1 / (_exponentialVolumeFactor - 1));

                            float currentVolume = ctrl.CurrentVolume;
                            if (Math.Abs(currentVolume - level) < 0.01f) continue;

                            // Keep UI responsive
                            ctrl.SmoothAndSetVolume(level, suppressEvent: !_allowVolumeApplication, disableSmoothing: _disableSmoothing);

                            if (_allowVolumeApplication)
                            {
                                ApplyVolumeToTargets(ctrl, targets, level);
                                ShowVolumeOverlay(); // Let the overlay handle its own focus prevention
                            }
                        }

                        // Enable volume application after first successful data processing
                        if (!_allowVolumeApplication)
                        {
                            _allowVolumeApplication = true;
                            _isInitializing = false;


                            SyncMuteStates();

                            if (!_timerCoordinator.IsMetersRunning)
                            {
                                _timerCoordinator.StartMeters();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                    }
                })); // REVERT: No DispatcherPriority specified = Normal priority
            }
            catch (Exception ex)
            {
            }
        }

        private void InitializeThemeSelector()
        {
            var themes = new List<ThemeOption>
            {
                new ThemeOption("Dark", "Dark", "🌑", "/Themes/DarkTheme.xaml"),
                new ThemeOption("Light", "Light", "☀️", "/Themes/LightTheme.xaml"),
                new ThemeOption("Arctic", "Arctic", "❄️", "/Themes/ArcticTheme.xaml"),
                new ThemeOption("Cyberpunk", "Cyberpunk", "🔮", "/Themes/CyberpunkTheme.xaml"),
                new ThemeOption("Dracula", "Dracula", "🧛", "/Themes/DraculaTheme.xaml"),
                new ThemeOption("Forest", "Forest", "🌲", "/Themes/ForestTheme.xaml"),
                new ThemeOption("Nord", "Nord", "🌌", "/Themes/NordTheme.xaml"),
                new ThemeOption("Ocean", "Ocean", "🌊", "/Themes/OceanTheme.xaml"),
                new ThemeOption("Sunset", "Sunset", "🌅", "/Themes/SunsetTheme.xaml"),
                new ThemeOption("Halloween", "Halloween", "🎃", "/Themes/HalloweenTheme.xaml"),
                new ThemeOption("Christmas", "Christmas", "🎄", "/Themes/ChristmasTheme.xaml"),
                new ThemeOption("Diwali", "Diwali", "🪔", "/Themes/DiwaliTheme.xaml"),
                new ThemeOption("Hanukkah", "Hanukkah", "🕎", "/Themes/HanukkahTheme.xaml"),
                new ThemeOption("Eid", "Eid", "🌙", "/Themes/EidTheme.xaml"),
                new ThemeOption("LunarNewYear", "Lunar New Year", "🧧", "/Themes/LunarNewYearTheme.xaml")
            };

            ThemeSelector.ItemsSource = themes;

            // Load saved theme or default to Dark
            string savedTheme = _settingsManager.AppSettings.SelectedTheme ?? "Dark";
            var selectedTheme = themes.FirstOrDefault(t => t.Name == savedTheme) ?? themes[0];
            ThemeSelector.SelectedItem = selectedTheme;
        }

        private void InvertSlider_Checked(object sender, RoutedEventArgs e)
        {
            _settingsManager.SaveInvertState(true);
        }

        private void InvertSlider_Unchecked(object sender, RoutedEventArgs e)
        {
            _settingsManager.SaveInvertState(false);
        }

        private bool IsInteractiveElement(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ButtonBase || source is ComboBox || source is TextBoxBase || source is PasswordBox || source is Slider)
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void LoadAvailablePorts()
        {
            try
            {
                var availablePorts = SerialPort.GetPortNames();
                string currentSelection = ComPortSelector.SelectedItem as string;

                ComPortSelector.ItemsSource = availablePorts;



                // Try to restore previous selection first
                if (!string.IsNullOrEmpty(currentSelection) && availablePorts.Contains(currentSelection))
                {
                    ComPortSelector.SelectedItem = currentSelection;

                }
                else if (!string.IsNullOrEmpty(_serialManager.LastConnectedPort) && availablePorts.Contains(_serialManager.LastConnectedPort))
                {
                    ComPortSelector.SelectedItem = _serialManager.LastConnectedPort;

                }
                else if (!string.IsNullOrEmpty(_settingsManager.AppSettings.PortName) && availablePorts.Contains(_settingsManager.AppSettings.PortName))
                {
                    ComPortSelector.SelectedItem = _settingsManager.AppSettings.PortName;

                }
                else if (availablePorts.Length > 0)
                {
                    // Don't auto-select first port - force user to manually select
                    // This prevents connecting to wrong devices on startup
                    ComPortSelector.SelectedIndex = -1;

                }
                else
                {
                    ComPortSelector.SelectedIndex = -1;

                }
            }
            catch (Exception ex)
            {

                ComPortSelector.ItemsSource = new string[0];
                ComPortSelector.SelectedIndex = -1;
            }
        }

        /// <summary>
        /// Loads all profiles into the profile selector ComboBox
        /// </summary>
        private void LoadProfilesIntoUI()
        {
            try
            {
                var profileNames = _profileManager.GetProfileNames();

                ProfileSelector.SelectionChanged -= ProfileSelector_SelectionChanged;
                ProfileSelector.ItemsSource = profileNames;
                ProfileSelector.SelectedItem = _profileManager.ActiveProfile.Name;
                ProfileSelector.SelectionChanged += ProfileSelector_SelectionChanged;


            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Profiles] Error loading profiles into UI: {ex.Message}");

            }
        }

        private void LoadSavedPortName()
        {
            try
            {
                string savedPort = _settingsManager.LoadSavedPortName();
                if (!string.IsNullOrWhiteSpace(savedPort))
                {

                }
            }
            catch (Exception ex)
            {

            }
        }

        private void LoadSettingsWithoutSerialConnection()
        {
            try
            {
                // Load settings from the active profile
                var settings = _profileManager.GetActiveProfileSettings();
                _settingsManager.AppSettings = settings;



                // Apply UI settings - apply saved theme if available
                if (!string.IsNullOrEmpty(settings.SelectedTheme))
                {
                    // Theme will be applied by InitializeThemeSelector
                    // which runs earlier and selects the correct theme
                }
                else
                {
                    // Fallback for old settings without SelectedTheme
                    ApplyTheme(settings.IsDarkTheme ? "Dark" : "Light");
                }
                InvertSliderCheckBox.IsChecked = settings.IsSliderInverted;
                ShowSlidersCheckBox.IsChecked = settings.VuMeters;

                SetMeterVisibilityForAll(settings.VuMeters);
                DisableSmoothingCheckBox.IsChecked = settings.DisableSmoothing;
                UseExponentialVolumeCheckBox.IsChecked = settings.UseExponentialVolume;

                ExponentialVolumeFactorSlider.Value = settings.ExponentialVolumeFactor;

                _settingsManager.ValidateOverlayPosition();

                // Handle startup settings
                StartOnBootCheckBox.Checked -= StartOnBootCheckBox_Checked;
                StartOnBootCheckBox.Unchecked -= StartOnBootCheckBox_Unchecked;

                bool isInStartup = _systemIntegrationService.IsStartupEnabled();
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

                // Initialize overlay service with settings
                _overlayService.UpdateSettings(settings);

                // If overlay is enabled and autohide is disabled, show it immediately after startup
                if (settings.OverlayEnabled && settings.OverlayTimeoutSeconds == AppSettings.OverlayNoTimeout)
                {
                    var startupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                    startupTimer.Tick += (s, e) =>
                    {
                        startupTimer.Stop();
                        ShowVolumeOverlay();

                    };
                    startupTimer.Start();
                }
            }
            catch (Exception ex)
            {

                _expectedSliderCount = 4;
                GenerateSliders(4);
                _hasLoadedInitialSettings = true;
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            // If user clicked the X button (not exiting via menu), minimize to tray instead
            if (!_isExiting)
            {
                e.Cancel = true; // Cancel the close operation
                this.WindowState = WindowState.Minimized;
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;


                return;
            }

            // Actually exiting - perform cleanup

            _isClosing = true;

            try
            {
                // Stop all timers
                _timerCoordinator?.Dispose();

                // Cleanup serial connection
                _serialManager?.Dispose();

                // Cleanup overlay
                _overlayService?.Dispose();

                // Dispose device enumerator
                _deviceEnumerator?.Dispose();

                // Final cleanup
                ServiceLocator.Dispose();
            }
            catch (Exception ex)
            {

            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SliderPanel.Visibility = Visibility.Visible;
            StartOnBootCheckBox.IsChecked = _settingsManager.AppSettings.StartOnBoot;

            // Autosize once when layout completes
            this.Dispatcher.BeginInvoke(() => AutoSizeToChannels(), DispatcherPriority.ApplicationIdle);


        }

        private void MaxButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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

        /// <summary>
        /// Creates a new profile
        /// </summary>
        private void NewProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Save current profile before creating new one
                SaveSettings();

                // Prompt for profile name
                string newName = InputDialog.Show("New Profile", "Enter a name for the new profile:", "", this);

                if (string.IsNullOrWhiteSpace(newName))
                    return;

                // Ask if they want to copy current settings
                var result = ConfirmationDialog.ShowYesNoCancel("Copy Settings?",
                    "Do you want to copy the current profile's settings to the new profile?\n\n" +
                    "Yes: Copy current settings\n" +
                    "No: Start with default settings\n" +
                    "Cancel: Don't create profile",
                    this);

                if (result == ConfirmationDialog.ButtonResult.Cancel)
                    return;

                bool copyFromActive = result == ConfirmationDialog.ButtonResult.Yes;

                if (_profileManager.CreateProfile(newName, copyFromActive))
                {
                    LoadProfilesIntoUI();
                    ProfileSelector.SelectedItem = newName;

                    ConfirmationDialog.ShowOK("Success",
                        $"Profile '{newName}' created successfully!", this);
                }
                else
                {
                    ConfirmationDialog.ShowOK("Error",
                        $"Failed to create profile '{newName}'. A profile with this name may already exist.", this);
                }
            }
            catch (Exception ex)
            {
                ConfirmationDialog.ShowOK("Error",
                    $"Error creating profile: {ex.Message}", this);
            }
        }

        private void OnOverlayPositionChanged(object sender, OverlayPositionChangedEventArgs e)
        {


            if (_settingsManager.AppSettings != null)
            {
                _settingsManager.AppSettings.OverlayX = e.X;
                _settingsManager.AppSettings.OverlayY = e.Y;

                // Save screen information for multi-monitor support
                var screenInfo = DeejNG.Core.Helpers.ScreenPositionManager.GetScreenInfo(e.X, e.Y);
                _settingsManager.AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                _settingsManager.AppSettings.OverlayScreenBounds = screenInfo.Bounds;



                // Trigger debounced save to settings.json (includes position AND screen info)
                _timerCoordinator.TriggerPositionSave();


            }
            else
            {

            }
        }

        private void OnSystemResumed(object sender, EventArgs e)
        {


            // Run reinit on UI thread with background priority to avoid focus stealing
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    // Refresh audio device references
                    RefreshAudioDevices();

                    // Restart timers
                    _timerCoordinator.StartMeters();
                    _timerCoordinator.StartSessionCache();

                    // Reconnect serial if it was connected before
                    if (!_serialManager.IsConnected && _serialManager.ShouldAttemptReconnect())
                    {

                        _timerCoordinator.StartSerialReconnect();
                    }

                    // Force session cache update
                    UpdateSessionCache();

                    // Re-sync mute states
                    _hasSyncedMuteStates = false;
                    SyncMuteStates();

                    // Re-enable volume application after short delay
                    var enableTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    enableTimer.Tick += (s, args) =>
                    {
                        enableTimer.Stop();
                        _allowVolumeApplication = true;

                    };
                    enableTimer.Start();


                }
                catch (Exception ex)
                {

                }
            }, DispatcherPriority.Background);
        }

        private void OnSystemResuming(object sender, EventArgs e)
        {


            // Prevent UI operations during early resume
            _allowVolumeApplication = false;
        }

        private void OnSystemSuspending(object sender, EventArgs e)
        {


            // Stop timers to prevent errors during sleep
            _timerCoordinator.StopMeters();
            _timerCoordinator.StopSessionCache();

            // Force save overlay position and settings before sleep
            if (_settingsManager?.AppSettings != null)
            {
                try
                {
                    _settingsManager.SaveSettings(_settingsManager.AppSettings);

                }
                catch (Exception ex)
                {

                }
            }

            // Hide overlay to prevent positioning issues
            _overlayService?.HideOverlay();
        }

        private void PositionSaveTimer_Tick(object sender, EventArgs e)
        {
            // Save directly - the save operation is fast enough and this timer
            // only fires occasionally (debounced). Using Task.Run was creating
            // unnecessary thread pool work items that accumulated over time.
            try
            {
                // Update active profile with current settings and save
                _profileManager.UpdateActiveProfileSettings(_settingsManager.AppSettings);
                _profileManager.SaveProfiles();

            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// Handles profile selection change
        /// </summary>
        private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || ProfileSelector.SelectedItem == null)
                return;

            string selectedProfile = ProfileSelector.SelectedItem.ToString();



            // CRITICAL FIX: Save current profile's settings before switching
            SaveSettings();

            if (_profileManager.SwitchToProfile(selectedProfile))
            {
                // Reload settings from the new profile
                LoadSettingsWithoutSerialConnection();

                // Update serial port if it changed
                string newPort = _profileManager.GetActiveProfileSettings().PortName;
                if (!string.IsNullOrEmpty(newPort))
                {
                    ComPortSelector.SelectedItem = newPort;
                }

                ConfirmationDialog.ShowOK("Profile Changed",
                    $"Switched to profile: {selectedProfile}", this);
            }
        }

        private void RefreshAudioDevices()
        {
            try
            {
                // Refresh default audio device
                _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _systemVolume = _audioDevice.AudioEndpointVolume;

                // Re-register volume notification
                _systemVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                _systemVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;

                // Refresh device caches
                _deviceManager.RefreshCaches();

                // Clear cached sessions
                _sessionIdCache.Clear();
                _cachedSessionsForMeters = null;
                _cachedAudioDevice = null;


            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// Renames the current profile
        /// </summary>
        private void RenameProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string currentName = _profileManager.ActiveProfile.Name;
                string newName = InputDialog.Show("Rename Profile",
                    $"Enter a new name for profile '{currentName}':", currentName, this);

                if (string.IsNullOrWhiteSpace(newName) || newName == currentName)
                    return;

                if (_profileManager.RenameProfile(currentName, newName))
                {
                    LoadProfilesIntoUI();

                    ConfirmationDialog.ShowOK("Success",
                        $"Profile renamed from '{currentName}' to '{newName}'", this);
                }
                else
                {
                    ConfirmationDialog.ShowOK("Error",
                        $"Failed to rename profile. A profile named '{newName}' may already exist.", this);
                }
            }
            catch (Exception ex)
            {
                ConfirmationDialog.ShowOK("Error",
                    $"Error renaming profile: {ex.Message}", this);
            }
        }

        private void SaveSettings()
        {
            if (_isInitializing || !_hasLoadedInitialSettings)
            {

                return;
            }

            try
            {
                if (_channelControls.Count == 0)
                {

                    return;
                }

                var sliderTargets = _channelControls.Select(c => c.AudioTargets ?? new List<AudioTarget>()).ToList();



                // BUGFIX: Preserve PortName from AppSettings instead of CurrentPort
                // CurrentPort is empty when not connected, which would wipe out the saved port
                string portToSave = !string.IsNullOrEmpty(_serialManager.CurrentPort)
                    ? _serialManager.CurrentPort
                    : _settingsManager.AppSettings.PortName;



                var settings = _settingsManager.CreateSettingsFromUI(
                    portToSave,
                    sliderTargets,
                    isDarkTheme,
                    InvertSliderCheckBox.IsChecked ?? false,
                    ShowSlidersCheckBox.IsChecked ?? true,
                    StartOnBootCheckBox.IsChecked ?? false,
                    StartMinimizedCheckBox.IsChecked ?? false,
                    DisableSmoothingCheckBox.IsChecked ?? false,
                    UseExponentialVolumeCheckBox.IsChecked ?? false,
                    (float)ExponentialVolumeFactorSlider.Value,
                    // BUGFIX: Use baud rate from AppSettings instead of CurrentBaudRate
                    // This ensures user's baud rate selection is preserved even if not connected
                    _settingsManager.AppSettings.BaudRate > 0 ? _settingsManager.AppSettings.BaudRate : _serialManager.CurrentBaudRate
                );

                // Update active profile and save to profiles.json
                _profileManager.UpdateActiveProfileSettings(settings);
                _profileManager.SaveProfiles();

                // BUGFIX: Also save to settings.json to maintain consistency
                // This ensures both profile system and legacy settings file stay in sync
                _settingsManager.SaveSettings(settings);
            }
            catch (Exception ex)
            {

            }
        }

        private void SerialReconnectTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing || !_serialManager.ShouldAttemptReconnect())
            {

                _timerCoordinator.StopSerialReconnect();
                return;
            }



            // Update UI to show attempting reconnection with background priority to prevent focus stealing
            Dispatcher.BeginInvoke(() =>
            {
                ConnectionStatus.Text = "Attempting to reconnect...";
                ConnectionStatus.Foreground = Brushes.Orange;
            }, DispatcherPriority.Background);

            if (_serialManager.TryConnectToSavedPort(
                _settingsManager.AppSettings.PortName,
                _settingsManager.AppSettings.BaudRate > 0 ? _settingsManager.AppSettings.BaudRate : 9600))
            {

                return; // The Connected event will stop the timer
            }

            // FIX: Don't blindly connect to first available port - wait for user selection
            // This prevents connecting to wrong devices (e.g., joysticks, other controllers)
            try
            {
                var availablePorts = SerialPort.GetPortNames();

                if (availablePorts.Length == 0)
                {

                    Dispatcher.BeginInvoke(() =>
                    {
                        ConnectionStatus.Text = "Waiting for device...";
                        ConnectionStatus.Foreground = Brushes.Orange;
                        LoadAvailablePorts();
                    }, DispatcherPriority.Background);
                    return;
                }



                // Update UI to prompt user to select the correct port
                Dispatcher.BeginInvoke(() =>
                {
                    ConnectionStatus.Text = $"Please select correct COM port ({availablePorts.Length} available)";
                    ConnectionStatus.Foreground = Brushes.Orange;
                    LoadAvailablePorts();
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {

                Dispatcher.BeginInvoke(() =>
                {
                    ConnectionStatus.Text = "Connection failed - check ports manually";
                    ConnectionStatus.Foreground = Brushes.Red;
                }, DispatcherPriority.Background);
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
            var settingsWindow = new SettingsWindow { Owner = this };
            settingsWindow.Show();
        }

        private void SetupAutomaticSerialConnection()
        {
            // Clear invalid ports list on startup to give saved port a fresh chance
            // This prevents issues where the Arduino might have been slow to respond in previous session
            _serialManager.ClearInvalidPorts();


            var connectionAttempts = 0;
            const int maxAttempts = 5;
            var attemptTimer = new DispatcherTimer();

            attemptTimer.Tick += (s, e) =>
            {
                connectionAttempts++;
                int baudRate = _settingsManager.AppSettings.BaudRate > 0 ? _settingsManager.AppSettings.BaudRate : 9600;


                if (_serialManager.TryConnectToSavedPort(_settingsManager.AppSettings.PortName, baudRate))
                {

                    attemptTimer.Stop();
                    Dispatcher.BeginInvoke(() => UpdateConnectionStatus(), DispatcherPriority.Background);
                    return;
                }

                if (connectionAttempts >= maxAttempts)
                {

                    attemptTimer.Stop();

                    // Start the auto-reconnect timer since initial attempts failed
                    if (_serialManager.ShouldAttemptReconnect())
                    {
                        _timerCoordinator.StartSerialReconnect();
                    }

                    Dispatcher.BeginInvoke(() => UpdateConnectionStatus(), DispatcherPriority.Background);
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

        // FIX: Safe overlay display that won't steal focus
        private void ShowVolumeOverlayIfSafe()
        {
            // Only show overlay if:
            // 1. Our main window is already active/focused, OR
            // 2. Overlay is set to always show (no timeout), OR
            // 3. Window is visible and not minimized
            if (this.IsActive ||
                _settingsManager.AppSettings.OverlayTimeoutSeconds == AppSettings.OverlayNoTimeout ||
                (this.IsVisible && this.WindowState != WindowState.Minimized))
            {
                ShowVolumeOverlay();
            }
            else
            {

            }
        }
        private void StartMinimizedCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settingsManager.AppSettings.StartMinimized = true;
            SaveSettings();
        }

        private void StartMinimizedCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settingsManager.AppSettings.StartMinimized = false;
            SaveSettings();
        }

        private void StartOnBootCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _systemIntegrationService.EnableStartup();
            _settingsManager.AppSettings.StartOnBoot = true;
            SaveSettings();
        }

        private void StartOnBootCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _systemIntegrationService.DisableStartup();
            _settingsManager.AppSettings.StartOnBoot = false;
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

                    }
                    catch (Exception ex)
                    {

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

                        }
                        catch (Exception ex)
                        {

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
                                ctrl.SetMuted(isMuted, applyToAudio: false);
                            }
                            continue;
                        }

                        string targetName = target.Name?.Trim().ToLower();
                        if (string.IsNullOrEmpty(targetName)) continue;

                        if (targetName == "system")
                        {
                            bool isMuted = audioDevice.AudioEndpointVolume.Mute;
                            ctrl.SetMuted(isMuted, applyToAudio: false);
                        }
                        else if (targetName == "unmapped")
                        {
                            var mappedApps = GetAllMappedApplications();
                            mappedApps.Remove("unmapped");
                            _audioService.ApplyMuteStateToUnmappedApplications(ctrl.IsMuted, mappedApps);
                            // In SyncMuteStates we only care about mute; don't touch meters here.
                            // Leave meter updates to UpdateMeters().
                        }
                        else
                        {
                            if (sessionsByProcess.TryGetValue(targetName, out var matchedSession))
                            {
                                try
                                {
                                    bool isMuted = matchedSession.SimpleAudioVolume.Mute;
                                    ctrl.SetMuted(isMuted, applyToAudio: false);
                                }
                                catch (ArgumentException) { }
                            }
                        }
                    }
                }

                _hasSyncedMuteStates = true;

            }
            catch (Exception ex)
            {

            }
        }

        private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || ThemeSelector.SelectedItem is not ThemeOption selectedTheme)
                return;

            try
            {
                // Remove existing theme dictionaries
                var themesToRemove = Application.Current.Resources.MergedDictionaries
                    .Where(d => d.Source != null && d.Source.OriginalString.Contains("/Themes/") && d.Source.OriginalString.EndsWith("Theme.xaml"))
                    .ToList();

                foreach (var theme in themesToRemove)
                {
                    Application.Current.Resources.MergedDictionaries.Remove(theme);
                }

                // Add the new theme (insert before Styles.xaml)
                var newTheme = new ResourceDictionary { Source = new Uri(selectedTheme.ThemeFile, UriKind.Relative) };
                var stylesIndex = Application.Current.Resources.MergedDictionaries
                    .Select((d, i) => new { Dict = d, Index = i })
                    .FirstOrDefault(x => x.Dict.Source?.OriginalString.Contains("Styles.xaml") == true);

                if (stylesIndex != null)
                {
                    Application.Current.Resources.MergedDictionaries.Insert(stylesIndex.Index, newTheme);
                }
                else
                {
                    Application.Current.Resources.MergedDictionaries.Add(newTheme);
                }

                // Save theme preference
                _settingsManager.AppSettings.SelectedTheme = selectedTheme.Name;
                SaveSettings();


            }
            catch (Exception ex)
            {

            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            // Do not start a drag when clicking interactive controls
            if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }

            try { DragMove(); } catch { }
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

        /// <summary>
        /// Updates the button indicator UI based on configured button mappings.
        /// Buttons are auto-detected from serial data (10000/10001 values).
        /// </summary>
        private void UpdateButtonIndicators()
        {
            // OPTIMIZATION: Execute directly if on UI thread, otherwise dispatch
            if (Dispatcher.CheckAccess())
            {
                UpdateButtonIndicatorsCore();
            }
            else
            {
                Dispatcher.BeginInvoke(UpdateButtonIndicatorsCore);
            }
        }

        /// <summary>
        /// Core implementation for updating button indicators.
        /// Must be called on UI thread.
        /// </summary>
        private void UpdateButtonIndicatorsCore()
        {
            try
            {
                var settings = _settingsManager.AppSettings;

                _buttonIndicators.Clear();

                // Show indicators for all configured button mappings
                if (settings?.ButtonMappings != null && settings.ButtonMappings.Count > 0)
                {
                    foreach (var mapping in settings.ButtonMappings.OrderBy(m => m.ButtonIndex))
                    {
                        // For mute buttons, initialize indicator to show current mute state
                        bool initialState = false;
                        bool isMuteAction = mapping.Action == ButtonAction.MuteChannel ||
                                           mapping.Action == ButtonAction.GlobalMute;

                        if (isMuteAction)
                        {
                            if (mapping.Action == ButtonAction.MuteChannel &&
                                mapping.TargetChannelIndex >= 0 &&
                                mapping.TargetChannelIndex < _channelControls.Count)
                            {
                                initialState = _channelControls[mapping.TargetChannelIndex].IsMuted;
                            }
                            else if (mapping.Action == ButtonAction.GlobalMute)
                            {
                                initialState = _channelControls.Any(c => c.IsMuted);
                            }
                        }

                        var indicator = new ButtonIndicatorViewModel
                        {
                            ButtonIndex = mapping.ButtonIndex,
                            Action = mapping.Action,
                            ActionText = GetButtonActionText(mapping),
                            Icon = GetButtonActionIcon(mapping.Action),
                            ToolTip = GetButtonActionTooltip(mapping),
                            IsPressed = initialState
                        };

                        _buttonIndicators.Add(indicator);
                    }

                    // Set ItemsSource only if not already set (first time initialization)
                    if (ButtonIndicatorsList.ItemsSource == null)
                    {
                        ButtonIndicatorsList.ItemsSource = _buttonIndicators;

                    }

                    ButtonIndicatorsPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    ButtonIndicatorsPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {

            }
        }

        private void UpdateConnectionStatus()
        {
            string statusText;
            Brush statusColor;

            if (_serialManager.IsConnected)
            {
                if (_serialManager.IsProtocolValidated)
                {
                    statusText = $"Connected to {_serialManager.CurrentPort}";
                    statusColor = Brushes.Green;
                }
                else
                {
                    statusText = $"Verifying {_serialManager.CurrentPort}...";
                    statusColor = Brushes.Orange;
                }
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

            // Ensure we're on the UI thread and use background priority to prevent focus stealing
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => UpdateConnectionStatus(), DispatcherPriority.Background);
                return;
            }

            ConnectionStatus.Text = statusText;
            ConnectionStatus.Foreground = statusColor;

            ConnectButton.IsEnabled = true;
            ConnectButton.Content = _serialManager.IsConnected ? "Disconnect" : "Connect";


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

                if (_cachedAudioDevice == null)
                    return;

                // Get sessions frequently for real-time response
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

                        foreach (var target in targets)
                        {
                            try
                            {
                                if (target.IsInputDevice)
                                {
                                    if (_deviceManager.TryGetInputPeak(target.Name, out var peak, out var muted))
                                    {
                                        if (peak > 0.01f)
                                        {
                                            ctrl.UpdateAudioMeter(peak * visualGain);
                                        }
                                        else
                                        {
                                            ctrl.UpdateAudioMeter(0);
                                        }
                                    }
                                }
                                else if (target.IsOutputDevice)
                                {
                                    if (_deviceManager.TryGetOutputPeak(target.Name, out var peak, out var muted))
                                    {
                                        if (peak > 0.01f)
                                        {
                                            ctrl.UpdateAudioMeter(peak * visualGain);
                                        }
                                        else
                                        {
                                            ctrl.UpdateAudioMeter(0);
                                        }
                                    }
                                }
                                else if (string.Equals(target.Name, "system", StringComparison.OrdinalIgnoreCase))
                                {
                                    float peak = _cachedAudioDevice.AudioMeterInformation.MasterPeakValue;
                                    float systemVol = _cachedAudioDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                                    peak *= systemVol * systemCalibrationFactor;

                                    ctrl.UpdateAudioMeter(peak > 0 ? peak * visualGain : 0);
                                }
                                else if (string.Equals(target.Name, "current", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Get the currently focused application
                                    string currentFocusTarget = AudioUtilities.GetCurrentFocusTarget();
                                    if (!string.IsNullOrEmpty(currentFocusTarget))
                                    {
                                        AudioSessionControl? matchingSession = FindSessionOptimized(sessions, currentFocusTarget.ToLowerInvariant());

                                        if (matchingSession != null)
                                        {
                                            try
                                            {
                                                float peak = matchingSession.AudioMeterInformation.MasterPeakValue;
                                                ctrl.UpdateAudioMeter(peak > 0 ? peak * visualGain : 0);
                                            }
                                            catch (ArgumentException) { }
                                        }
                                    }
                                }
                                else if (string.Equals(target.Name, "unmapped", StringComparison.OrdinalIgnoreCase))
                                {
                                    var mappedApps = GetAllMappedApplications();
                                    mappedApps.Remove("unmapped");

                                    float unmappedPeak = GetUnmappedApplicationsPeakLevelOptimized(mappedApps, sessions);
                                    ctrl.UpdateAudioMeter(unmappedPeak > 0 ? unmappedPeak * visualGain : 0);
                                }
                                else
                                {
                                    AudioSessionControl? matchingSession = FindSessionOptimized(sessions, target.Name.ToLowerInvariant());

                                    if (matchingSession != null)
                                    {
                                        try
                                        {
                                            float peak = matchingSession.AudioMeterInformation.MasterPeakValue;
                                            ctrl.UpdateAudioMeter(peak > 0 ? peak * visualGain : 0);
                                        }
                                        catch (ArgumentException) { }
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

                        ctrl.UpdateAudioMeter(0);
                    }
                }
            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// Updates mute button indicators to reflect current channel mute states.
        /// Optimized to avoid unnecessary dispatcher calls and only update when state actually changes.
        /// </summary>
        private void UpdateMuteButtonIndicators()
        {
            // OPTIMIZATION: If already on UI thread, execute directly to avoid creating
            // additional dispatcher operations (which can accumulate handles over time)
            if (Dispatcher.CheckAccess())
            {
                UpdateMuteButtonIndicatorsCore();
            }
            else
            {
                Dispatcher.BeginInvoke(UpdateMuteButtonIndicatorsCore);
            }
        }

        /// <summary>
        /// Core implementation for updating mute button indicators.
        /// Must be called on UI thread.
        /// </summary>
        private void UpdateMuteButtonIndicatorsCore()
        {
            try
            {
                var settings = _settingsManager.AppSettings;
                if (settings?.ButtonMappings == null) return;

                foreach (var mapping in settings.ButtonMappings)
                {
                    bool isMuteAction = mapping.Action == ButtonAction.MuteChannel ||
                                       mapping.Action == ButtonAction.GlobalMute;

                    if (!isMuteAction) continue;

                    var indicator = _buttonIndicators.FirstOrDefault(b => b.ButtonIndex == mapping.ButtonIndex);
                    if (indicator == null) continue;

                    bool muteState = false;

                    if (mapping.Action == ButtonAction.MuteChannel &&
                        mapping.TargetChannelIndex >= 0 &&
                        mapping.TargetChannelIndex < _channelControls.Count)
                    {
                        muteState = _channelControls[mapping.TargetChannelIndex].IsMuted;
                    }
                    else if (mapping.Action == ButtonAction.GlobalMute)
                    {
                        muteState = _channelControls.Any(c => c.IsMuted);
                    }

                    // OPTIMIZATION: Only update if state actually changed to avoid
                    // triggering unnecessary PropertyChanged events and binding updates
                    if (indicator.IsPressed != muteState)
                    {
                        indicator.IsPressed = muteState;
                    }
                }
            }
            catch (Exception ex)
            {

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

            }
        }

        private void UseExponentialVolumeCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _useExponentialVolume = true;
            SaveSettings();
        }

        private void UseExponentialVolumeCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _useExponentialVolume = false;
            SaveSettings();
        }

        #endregion Private Methods

        #region Public Classes

        public class DecoupledAudioSessionEventsHandler : IAudioSessionEventsHandler
        {
            #region Private Fields

            private readonly MainWindow _mainWindow;
            private readonly string _targetName;

            #endregion Private Fields

            #region Public Constructors

            public DecoupledAudioSessionEventsHandler(MainWindow mainWindow, string targetName)
            {
                _mainWindow = mainWindow;
                _targetName = targetName;
            }

            #endregion Public Constructors

            #region Public Methods

            public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex)
            { }

            public void OnDisplayNameChanged(string displayName)
            { }

            public void OnGroupingParamChanged(ref Guid groupingId)
            { }

            public void OnIconPathChanged(string iconPath)
            { }

            public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {

                    _mainWindow.HandleSessionDisconnected(_targetName);
                });
            }

            public void OnSimpleVolumeChanged(float volume, bool mute)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var control = _mainWindow.FindControlForTarget(_targetName);
                    control?.SetMuted(mute, applyToAudio: false);
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
                    control?.SetMuted(mute, applyToAudio: false);
                });
            }

            #endregion Public Methods
        }

        #endregion Public Classes
    }

    internal static class IconHandler
    {
        #region Private Properties

        private static string IconPath => Path.Combine(AppContext.BaseDirectory, "icon.ico");

        #endregion Private Properties

        #region Public Methods

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

        #endregion Public Methods
    }
}