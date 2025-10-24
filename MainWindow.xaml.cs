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
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        #region Public Fields

        public List<ChannelControl> _channelControls = new();

        #endregion Public Fields

        #region Private Fields
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
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
        private readonly ProfileManager _profileManager;
        private readonly SerialConnectionManager _serialManager;
        private readonly TimerCoordinator _timerCoordinator;
        private readonly IOverlayService _overlayService;
        private readonly ISystemIntegrationService _systemIntegrationService;
        private readonly IPowerManagementService _powerManagementService;

        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            _isInitializing = true;
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            InitializeComponent();
            Loaded += MainWindow_Loaded;

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

            StartSessionCacheUpdater();

            // Start timers
            _timerCoordinator.StartSerialWatchdog();

            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            IconHandler.AddIconToRemovePrograms("DeejNG");
            _systemIntegrationService.SetDisplayIcon();

            // CRITICAL: Initialize overlay and subscribe to events BEFORE potentially hiding the window
            // This ensures the subscription happens even when starting minimized
#if DEBUG
            Debug.WriteLine("[MainWindow] Initializing overlay service in constructor");
            Debug.WriteLine($"[MainWindow] Overlay position from settings: X={_settingsManager.AppSettings.OverlayX}, Y={_settingsManager.AppSettings.OverlayY}");
#endif
            
            _overlayService.Initialize();
            
            // Subscribe to PositionChanged event
            _overlayService.PositionChanged += OnOverlayPositionChanged;
#if DEBUG
            Debug.WriteLine("[MainWindow] Subscribed to OverlayService.PositionChanged in constructor");
#endif
            
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
#if DEBUG
                    Debug.WriteLine($"[MainWindow] Removed handler for disconnected session: {targetName}");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[MainWindow] Error handling session disconnect for {targetName}: {ex.Message}");
#endif
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
#if DEBUG
                Debug.WriteLine("[Overlay] Skipping overlay to prevent focus stealing");
#endif
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
        }

        #endregion Public Methods

        #region Protected Methods

        #region Power Management Event Handlers

        private void OnSystemSuspending(object sender, EventArgs e)
        {
#if DEBUG
            Debug.WriteLine("[Power] System suspending - preparing for sleep");
#endif
            
            // Stop timers to prevent errors during sleep
            _timerCoordinator.StopMeters();
            _timerCoordinator.StopSessionCache();
            
            // Force save overlay position and settings before sleep
            if (_settingsManager?.AppSettings != null)
            {
                try
                {
                    _settingsManager.SaveSettings(_settingsManager.AppSettings);
#if DEBUG
                    Debug.WriteLine("[Power] Settings saved before sleep");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[Power] Error saving settings before sleep: {ex.Message}");
#endif
                }
            }
            
            // Hide overlay to prevent positioning issues
            _overlayService?.HideOverlay();
        }

        private void OnSystemResuming(object sender, EventArgs e)
        {
#if DEBUG
            Debug.WriteLine("[Power] System resuming - entering stabilization");
#endif
            
            // Prevent UI operations during early resume
            _allowVolumeApplication = false;
        }

        private void OnSystemResumed(object sender, EventArgs e)
        {
#if DEBUG
            Debug.WriteLine("[Power] System resume stabilized - reinitializing");
#endif
            
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
#if DEBUG
                        Debug.WriteLine("[Power] Starting serial reconnection after resume");
#endif
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
#if DEBUG
                        Debug.WriteLine("[Power] Volume application re-enabled");
#endif
                    };
                    enableTimer.Start();
                    
#if DEBUG
                    Debug.WriteLine("[Power] Reinitialization complete");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[Power] Error during reinitialization: {ex.Message}");
#endif
                }
            }, DispatcherPriority.Background);
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
                
#if DEBUG
                Debug.WriteLine("[Power] Audio devices refreshed");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[Power] Error refreshing audio devices: {ex.Message}");
#endif
            }
        }

        #endregion Power Management Event Handlers

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;

            // Force save settings (including overlay position) before shutdown
            if (_settingsManager?.AppSettings != null)
            {
                try
                {
                    _settingsManager.SaveSettings(_settingsManager.AppSettings);
#if DEBUG
                    Debug.WriteLine("[Shutdown] Settings force saved");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[Shutdown] Error force saving settings: {ex.Message}");
#endif
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
#if DEBUG
                    Debug.WriteLine($"[Cleanup] Unregistered event handler for {target} on application close");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[ERROR] Failed to unregister handler for {target} on close: {ex.Message}");
#endif
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
#if DEBUG
                        Debug.WriteLine($"[Cleanup] Released session COM object, final ref count: {refCount}");
#endif
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
#if DEBUG
                Debug.WriteLine($"[ERROR] Applying theme '{theme}': {ex.Message}");
#endif
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
#if DEBUG
                    Debug.WriteLine($"[ERROR] Applying volume to {target.Name}: {ex.Message}");
#endif
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
        public void SaveOverlayPosition(double x, double y)
        {
#if DEBUG
            Debug.WriteLine($"[MainWindow] SaveOverlayPosition called: X={x}, Y={y}");
#endif

            // Update settings with position AND screen information
            if (_settingsManager.AppSettings != null)
            {
                _settingsManager.AppSettings.OverlayX = x;
                _settingsManager.AppSettings.OverlayY = y;
                
                // Save screen information for multi-monitor support
                var screenInfo = DeejNG.Core.Helpers.ScreenPositionManager.GetScreenInfo(x, y);
                _settingsManager.AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                _settingsManager.AppSettings.OverlayScreenBounds = screenInfo.Bounds;
                
#if DEBUG
                Debug.WriteLine($"[MainWindow] Screen info captured: Device={screenInfo.DeviceName}, Bounds={screenInfo.Bounds}");
#endif

                // Save directly to settings.json - no debouncing needed since this only fires on mouse release
                _settingsManager.SaveSettings(_settingsManager.AppSettings);

#if DEBUG
                Debug.WriteLine($"[MainWindow] Position and screen info saved to settings.json");
#endif
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
#if DEBUG
                    Debug.WriteLine($"[Cleanup] Removed stale event handler for {target}");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[Cleanup] Error cleaning event handlers: {ex.Message}");
#endif
            }
        }

        private void ComPortSelector_DropDownOpened(object sender, EventArgs e)
        {
#if DEBUG
            Debug.WriteLine("[UI] COM port dropdown opened, refreshing ports...");
#endif
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
#if DEBUG
                    Debug.WriteLine($"[Manual] User clicked connect for port: {selectedPort}");
#endif

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
#if DEBUG
                Debug.WriteLine($"[ERROR] Creating notify icon context menu: {ex.Message}");
#endif
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
            Application.Current.Shutdown();
        }

        private void ForceCleanupTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing) return;

            try
            {
#if DEBUG
                Debug.WriteLine("[ForceCleanup] Starting aggressive cleanup...");
#endif
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
#if DEBUG
                Debug.WriteLine("[ForceCleanup] Cleanup completed");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ForceCleanup] Error: {ex.Message}");
#endif
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

#if DEBUG
            Debug.WriteLine("[Init] Generating sliders - ALL volume operations DISABLED until first data");
            Debug.WriteLine($"[Profiles] Loading {savedTargetGroups.Count} slider target groups from profile '{_profileManager.ActiveProfile.Name}'");
            for (int i = 0; i < savedTargetGroups.Count; i++)
            {
                var targets = savedTargetGroups[i];
                Debug.WriteLine($"[Profiles] Slider {i}: {targets.Count} targets - {string.Join(", ", targets.Select(t => t.Name))}");
            }
#endif

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
#if DEBUG
                    Debug.WriteLine($"[MainWindow] Received session disconnected for {target}");
#endif
                    if (_registeredHandlers.TryGetValue(target, out var handler))
                    {
                        _registeredHandlers.Remove(target);
#if DEBUG
                        Debug.WriteLine($"[MainWindow] Removed handler for disconnected session: {target}");
#endif
                    }
                };

                _channelControls.Add(control);
                SliderPanel.Children.Add(control);
            }

            SetMeterVisibilityForAll(ShowSlidersCheckBox.IsChecked ?? true);

#if DEBUG
            Debug.WriteLine("[Init] Sliders generated, waiting for first hardware data before completing initialization");
#endif
        }

        private void HandleSliderData(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                if (!data.Contains('|') && !float.TryParse(data, out _))
                {
#if DEBUG
                    Debug.WriteLine($"[Serial] Invalid data format: {data}");
#endif
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
#if DEBUG
                            Debug.WriteLine($"[INFO] Hardware has {parts.Length} sliders but app has {_channelControls.Count}. Adjusting to match hardware.");
#endif

                            var currentTargets = _channelControls.Select(c => c.AudioTargets).ToList();
                            _expectedSliderCount = parts.Length;
                            GenerateSliders(parts.Length);

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
                            if (!float.TryParse(parts[i].Trim(), out float level)) continue;

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

#if DEBUG
                            Debug.WriteLine("[Init] First data received - enabling volume application and completing setup");
#endif

                            SyncMuteStates();

                            if (!_timerCoordinator.IsMetersRunning)
                            {
                                _timerCoordinator.StartMeters();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine($"[ERROR] Processing slider data in UI thread: {ex.Message}");
#endif
                    }
                })); // REVERT: No DispatcherPriority specified = Normal priority
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Parsing slider data: {ex.Message}");
#endif
            }
        }
        private void LoadAvailablePorts()
        {
            try
            {
                var availablePorts = SerialPort.GetPortNames();
                string currentSelection = ComPortSelector.SelectedItem as string;

                ComPortSelector.ItemsSource = availablePorts;

#if DEBUG
                Debug.WriteLine($"[Ports] Found {availablePorts.Length} ports: [{string.Join(", ", availablePorts)}]");
#endif

                // Try to restore previous selection first
                if (!string.IsNullOrEmpty(currentSelection) && availablePorts.Contains(currentSelection))
                {
                    ComPortSelector.SelectedItem = currentSelection;
#if DEBUG
                    Debug.WriteLine($"[Ports] Restored previous selection: {currentSelection}");
#endif
                }
                else if (!string.IsNullOrEmpty(_serialManager.LastConnectedPort) && availablePorts.Contains(_serialManager.LastConnectedPort))
                {
                    ComPortSelector.SelectedItem = _serialManager.LastConnectedPort;
#if DEBUG
                    Debug.WriteLine($"[Ports] Selected saved port: {_serialManager.LastConnectedPort}");
#endif
                }
                else if (!string.IsNullOrEmpty(_settingsManager.AppSettings.PortName) && availablePorts.Contains(_settingsManager.AppSettings.PortName))
                {
                    ComPortSelector.SelectedItem = _settingsManager.AppSettings.PortName;
#if DEBUG
                    Debug.WriteLine($"[Ports] Selected settings port: {_settingsManager.AppSettings.PortName}");
#endif
                }
                else if (availablePorts.Length > 0)
                {
                    ComPortSelector.SelectedIndex = 0;
#if DEBUG
                    Debug.WriteLine($"[Ports] Selected first available port: {availablePorts[0]}");
#endif
                }
                else
                {
                    ComPortSelector.SelectedIndex = -1;
#if DEBUG
                    Debug.WriteLine("[Ports] No ports available");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to load available ports: {ex.Message}");
#endif
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
#if DEBUG
                    Debug.WriteLine($"[Settings] Loaded saved port name: {savedPort}");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to load saved port name: {ex.Message}");
#endif
            }
        }

        private void LoadSettingsWithoutSerialConnection()
        {
            try
            {
                // Load settings from the active profile
                var settings = _profileManager.GetActiveProfileSettings();
                _settingsManager.AppSettings = settings;

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
#if DEBUG
                        Debug.WriteLine("[Startup] Overlay shown automatically (autohide disabled)");
#endif
                    };
                    startupTimer.Start();
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to load settings without serial: {ex.Message}");
#endif
                _expectedSliderCount = 4;
                GenerateSliders(4);
                _hasLoadedInitialSettings = true;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SliderScrollViewer.Visibility = Visibility.Visible;
            StartOnBootCheckBox.IsChecked = _settingsManager.AppSettings.StartOnBoot;
            
#if DEBUG
            Debug.WriteLine("[MainWindow] MainWindow_Loaded - overlay already initialized in constructor");
#endif
        }

        private void OnOverlayPositionChanged(object sender, OverlayPositionChangedEventArgs e)
        {
#if DEBUG
            Debug.WriteLine($"[MainWindow] OnOverlayPositionChanged called: X={e.X}, Y={e.Y}");
#endif
            
            if (_settingsManager.AppSettings != null)
            {
                _settingsManager.AppSettings.OverlayX = e.X;
                _settingsManager.AppSettings.OverlayY = e.Y;
                
                // Save screen information for multi-monitor support
                var screenInfo = DeejNG.Core.Helpers.ScreenPositionManager.GetScreenInfo(e.X, e.Y);
                _settingsManager.AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                _settingsManager.AppSettings.OverlayScreenBounds = screenInfo.Bounds;
                
#if DEBUG
                Debug.WriteLine($"[MainWindow] Screen info captured: Device={screenInfo.DeviceName}, Bounds={screenInfo.Bounds}");
#endif
                
                // Trigger debounced save to settings.json (includes position AND screen info)
                _timerCoordinator.TriggerPositionSave();
                
#if DEBUG
                Debug.WriteLine($"[Overlay] Position and screen info queued for save to settings.json: X={e.X}, Y={e.Y}");
#endif
            }
            else
            {
#if DEBUG
                Debug.WriteLine("[MainWindow] AppSettings is null, cannot save position");
#endif
            }
        }

        private void PositionSaveTimer_Tick(object sender, EventArgs e)
        {
            // Save to disk on background thread
            Task.Run(() =>
            {
                try
                {
                    _settingsManager.SaveSettingsAsync(_settingsManager.AppSettings);
#if DEBUG
                    Debug.WriteLine("[Overlay] Position saved to disk (debounced)");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[ERROR] Failed to save overlay position: {ex.Message}");
#endif
                }
            });
        }

        private void SaveSettings()
        {
            if (_isInitializing || !_hasLoadedInitialSettings)
            {
#if DEBUG
                Debug.WriteLine("[Settings] Skipping save during initialization");
#endif
                return;
            }

            try
            {
                if (_channelControls.Count == 0)
                {
#if DEBUG
                    Debug.WriteLine("[Settings] Skipping save - no channel controls");
#endif
                    return;
                }

                var sliderTargets = _channelControls.Select(c => c.AudioTargets ?? new List<AudioTarget>()).ToList();

#if DEBUG
                Debug.WriteLine($"[Profiles] Saving settings to profile '{_profileManager.ActiveProfile.Name}'");
                for (int i = 0; i < sliderTargets.Count; i++)
                {
                    var targets = sliderTargets[i];
                    Debug.WriteLine($"[Profiles] Saving Slider {i}: {targets.Count} targets - {string.Join(", ", targets.Select(t => t.Name))}");
                }
#endif

                var settings = _settingsManager.CreateSettingsFromUI(
                    _serialManager.CurrentPort,
                    sliderTargets,
                    isDarkTheme,
                    InvertSliderCheckBox.IsChecked ?? false,
                    ShowSlidersCheckBox.IsChecked ?? true,
                    StartOnBootCheckBox.IsChecked ?? false,
                    StartMinimizedCheckBox.IsChecked ?? false,
                    DisableSmoothingCheckBox.IsChecked ?? false
                );

                // Update active profile and save
                _profileManager.UpdateActiveProfileSettings(settings);
                _profileManager.SaveProfiles();
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
#endif
            }
        }

        private void SerialReconnectTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing || !_serialManager.ShouldAttemptReconnect())
            {
#if DEBUG
                Debug.WriteLine("[SerialReconnect] Stopping reconnect - closing or manual disconnect");
#endif
                _timerCoordinator.StopSerialReconnect();
                return;
            }

#if DEBUG
            Debug.WriteLine("[SerialReconnect] Attempting to reconnect...");
#endif

            // Update UI to show attempting reconnection with background priority to prevent focus stealing
            Dispatcher.BeginInvoke(() =>
            {
                ConnectionStatus.Text = "Attempting to reconnect...";
                ConnectionStatus.Foreground = Brushes.Orange;
            }, DispatcherPriority.Background);

            if (_serialManager.TryConnectToSavedPort(_settingsManager.AppSettings.PortName))
            {
#if DEBUG
                Debug.WriteLine("[SerialReconnect] Successfully reconnected to saved port");
#endif
                return; // The Connected event will stop the timer
            }

            // Try any available port if saved port doesn't work
            try
            {
                var availablePorts = SerialPort.GetPortNames();

                if (availablePorts.Length == 0)
                {
#if DEBUG
                    Debug.WriteLine("[SerialReconnect] No serial ports available");
#endif
                    Dispatcher.BeginInvoke(() =>
                    {
                        ConnectionStatus.Text = "Waiting for device...";
                        ConnectionStatus.Foreground = Brushes.Orange;
                        LoadAvailablePorts();
                    }, DispatcherPriority.Background);
                    return;
                }

                string portToTry = availablePorts[0];
#if DEBUG
                Debug.WriteLine($"[SerialReconnect] Trying first available port: {portToTry}");
#endif

                _serialManager.InitSerial(portToTry, 9600);

                if (_serialManager.IsConnected)
                {
#if DEBUG
                    Debug.WriteLine($"[SerialReconnect] Successfully connected to {portToTry}");
#endif
                    // The Connected event will handle UI updates and stop the timer
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[SerialReconnect] Failed to reconnect: {ex.Message}");
#endif
                Dispatcher.BeginInvoke(() =>
                {
                    ConnectionStatus.Text = "Reconnection failed - retrying...";
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
#if DEBUG
                Debug.WriteLine($"[ERROR] Session cache update: {ex.Message}");
#endif
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
#if DEBUG
                Debug.WriteLine($"[AutoConnect] Attempt #{connectionAttempts}");
#endif

                if (_serialManager.TryConnectToSavedPort(_settingsManager.AppSettings.PortName))
                {
#if DEBUG
                    Debug.WriteLine("[AutoConnect] Successfully connected!");
#endif
                    attemptTimer.Stop();
                    Dispatcher.BeginInvoke(() => UpdateConnectionStatus(), DispatcherPriority.Background);
                    return;
                }

                if (connectionAttempts >= maxAttempts)
                {
#if DEBUG
                    Debug.WriteLine($"[AutoConnect] Failed after {maxAttempts} attempts - starting auto-reconnect timer");
#endif
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
#if DEBUG
                Debug.WriteLine("[Sync] Skipping SyncMuteStates - volume application disabled");
#endif
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
#if DEBUG
                        Debug.WriteLine($"[Sync] Unregistered handler for {target}");
#endif
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine($"[ERROR] Failed to unregister handler for {target}: {ex.Message}");
#endif
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
#if DEBUG
                        Debug.WriteLine($"[ERROR] Mapping session in SyncMuteStates: {ex.Message}");
#endif
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
#if DEBUG
                            Debug.WriteLine($"[Event] Registered DECOUPLED handler for {targetName} (PID: {sessionProcessIds[targetName]})");
#endif
                        }
                        catch (Exception ex)
                        {
#if DEBUG
                            Debug.WriteLine($"[ERROR] Registering handler for {targetName}: {ex.Message}");
#endif
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
#if DEBUG
                                    Debug.WriteLine($"[ERROR] Getting mute state for {targetName}: {ex.Message}");
#endif
                                }
                            }
                        }
                    }
                }

                _hasSyncedMuteStates = true;
#if DEBUG
                Debug.WriteLine($"[Sync] Registered {_registeredHandlers.Count} decoupled handlers for unique targets");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] In SyncMuteStates: {ex.Message}");
#endif
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

#if DEBUG
            Debug.WriteLine($"[Status] {statusText}");
#endif
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
#if DEBUG
                                Debug.WriteLine($"[ERROR] Processing target {target.Name}: {ex.Message}");
#endif
                            }
                        }

                        float finalLevel = ctrl.IsMuted || allMuted ? 0 : Math.Min(highestPeak * visualGain, 1.0f);
                        ctrl.UpdateAudioMeter(finalLevel);
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine($"[ERROR] Control meter update: {ex.Message}");
#endif
                        ctrl.UpdateAudioMeter(0);
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] UpdateMeters: {ex.Message}");
#endif
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
#if DEBUG
                    Debug.WriteLine($"[ERROR] Processing session in cache updater: {ex.Message}");
#endif
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
#if DEBUG
                Debug.WriteLine($"[ERROR] Updating session cache entry: {ex.Message}");
#endif
            }
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
#if DEBUG
                Debug.WriteLine($"[ERROR] Finding session optimized: {ex.Message}");
#endif
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
#if DEBUG
                Debug.WriteLine($"[ERROR] Getting unmapped peak levels optimized: {ex.Message}");
#endif
            }

            return highestPeak;
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

        #region Profile Management

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

#if DEBUG
                Debug.WriteLine($"[Profiles] Loaded {profileNames.Count} profiles into UI");
                Debug.WriteLine($"[Profiles] Active: {_profileManager.ActiveProfile.Name}");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[Profiles] Error loading profiles into UI: {ex.Message}");
#endif
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

#if DEBUG
            Debug.WriteLine($"[Profiles] User selected profile: {selectedProfile}");
#endif

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

        #endregion Profile Management

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
#if DEBUG
                    Debug.WriteLine($"[DecoupledHandler] Session disconnected for {_targetName}: {disconnectReason}");
#endif
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
