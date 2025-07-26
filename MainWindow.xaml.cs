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
using DeejNG.Models;
using static System.Windows.Forms.Design.AxImporter;
using System.Runtime.InteropServices;
using System.Windows.Media;
using DeejNG.Views;

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        #region Public Fields

        // You'll also need to make _channelControls accessible to allow the ChannelControl to get its index
        // In MainWindow.xaml.cs, change the private field to:
        public List<ChannelControl> _channelControls = new();

        public FloatingOverlay _overlay;

        #endregion Public Fields

        #region Private Fields

        private const int CALIBRATION_DELAY_MS = 500;
        private const int METER_SESSION_CACHE_MS = 1000;
        private static readonly System.Text.RegularExpressions.Regex _invalidSerialCharsRegex = new System.Text.RegularExpressions.Regex(@"[^\x20-\x7E\r\n]", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Object pools to reduce allocations
        private readonly Queue<List<AudioTarget>> _audioTargetListPool = new();

        private readonly HashSet<string> _cachedMappedApplications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private readonly Dictionary<string, float> _lastInputVolume = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _settingsLock = new object();
        private readonly Queue<HashSet<string>> _stringHashSetPool = new();
        private readonly HashSet<string> _tempStringSet = new(StringComparer.OrdinalIgnoreCase);
        // Cached collections to avoid repeated allocations
        private readonly List<AudioTarget> _tempTargetList = new();

        private readonly object _unmappedLock = new object();
        private readonly TimeSpan DEVICE_CACHE_DURATION = TimeSpan.FromSeconds(5);
        private readonly TimeSpan UNMAPPED_THROTTLE_INTERVAL = TimeSpan.FromMilliseconds(100);
        // 500ms calibration period
        private bool _allowVolumeApplication = false;

        private AppSettings _appSettings = new();
        private MMDevice _audioDevice;
        private AudioService _audioService;
        private MMDevice _cachedAudioDevice;
        private SessionCollection _cachedSessionsForMeters;
        private float _cachedUnmappedPeak = 0;
        private DateTime _calibrationStartTime = DateTime.MinValue;
        private bool _disableSmoothing = false;
        private int _expectedSliderCount = -1;
        private bool _expectingData = false;
        private DispatcherTimer _forceCleanupTimer;
        private bool _hasLoadedInitialSettings = false;
        private bool _hasReceivedInitialData = false;
        // Track expected number of sliders
        private bool _hasReceivedInitialSerialData = false;

        private bool _hasSyncedMuteStates = false;
        private Dictionary<int, float> _initialSliderValues = new();
        private Dictionary<string, MMDevice> _inputDeviceMap = new();
        private bool _isCalibrating = false;
        private bool _isClosing = false;
        private bool _isConnected = false;
        private bool _isInitializing = true;
        private string _lastConnectedPort = string.Empty;
        private DateTime _lastDeviceCacheTime = DateTime.MinValue;
        private DateTime _lastDeviceRefresh = DateTime.MinValue;
        private DateTime _lastForcedCleanup = DateTime.MinValue;
        // Increase throttling
        private DateTime _lastMappedApplicationsUpdate = DateTime.MinValue;

        private DateTime _lastMeterSessionRefresh = DateTime.MinValue;
        private DateTime _lastSessionRefresh = DateTime.MinValue;
        private DateTime _lastSettingsSave = DateTime.MinValue;
        private DateTime _lastUnmappedMeterUpdate = DateTime.MinValue;
        private float _lastUnmappedPeak = 0;
        // Cache unmapped peak calculations
        private DateTime _lastUnmappedPeakCalculation = DateTime.MinValue;

        private DateTime _lastUnmappedPeakUpdate = DateTime.MinValue;
        private DateTime _lastValidDataTimestamp = DateTime.MinValue;
        private bool _manualDisconnect = false;
        private bool _metersEnabled = true;
        private int _meterSkipCounter = 0;
        private DispatcherTimer _meterTimer;
        private int _noDataCounter = 0;
        private Dictionary<string, MMDevice> _outputDeviceMap = new();
        private DispatcherTimer _positionSaveTimer;
        private Dictionary<int, string> _processNameCache = new();
        private Dictionary<string, IAudioSessionEventsHandler> _registeredHandlers = new();
        private StringBuilder _serialBuffer = new();
        private bool _serialDisconnected = false;
        private SerialPort _serialPort;
        private bool _serialPortFullyInitialized = false;
        private DispatcherTimer _serialReconnectTimer;
        private DispatcherTimer _serialWatchdogTimer;
        private int _sessionCacheHitCount = 0;
        private DispatcherTimer _sessionCacheTimer;
        private List<(AudioSessionControl session, string sessionId, string instanceId)> _sessionIdCache = new();

        //  private Dictionary<string, AudioSessionControl> _sessionLookup = new();
        private AudioEndpointVolume _systemVolume;
        private string _userSelectedPort = string.Empty;

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
            BuildOutputDeviceCache();

            // Load the saved port name from settings BEFORE populating ports
            LoadSavedPortName();

            // Load ports AFTER loading the saved port name so ComboBox can select the correct port
            LoadAvailablePorts();

            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _systemVolume = _audioDevice.AudioEndpointVolume;
            _systemVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;

            _meterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(25) // Much more responsive - 40 FPS
            };
            _meterTimer.Tick += UpdateMeters;
            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            StartSessionCacheUpdater();

            // Initialize serial reconnect timer
            _serialReconnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _serialReconnectTimer.Tick += SerialReconnectTimer_Tick;

            // Initialize the watchdog timer
            _serialWatchdogTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _serialWatchdogTimer.Tick += SerialWatchdogTimer_Tick;
            _serialWatchdogTimer.Start();

            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            IconHandler.AddIconToRemovePrograms("DeejNG");
            SetDisplayIcon();

            // Load settings but don't auto-connect to serial port yet
            LoadSettingsWithoutSerialConnection();

            _isInitializing = false;
            if (_appSettings.StartMinimized)
            {
                WindowState = WindowState.Minimized;
                Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }
            _forceCleanupTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(5) // More frequent cleanup
            };
            _forceCleanupTimer.Tick += ForceCleanupTimer_Tick;
            _forceCleanupTimer.Start();
            // IMPROVED: Multiple attempts to connect with better timing
            SetupAutomaticSerialConnection();
        }

        #endregion Public Constructors

        #region Private Properties

        private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeejNG", "settings.json");

        #endregion Private Properties

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

        public Dictionary<int, List<AudioTarget>> GetAllAssignedTargets()
        {
            var result = new Dictionary<int, List<AudioTarget>>();

            for (int i = 0; i < _channelControls.Count; i++)
            {
                result[i] = new List<AudioTarget>(_channelControls[i].AudioTargets);
            }

            return result;
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

        // Add this helper method to MainWindow.xaml.cs
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

        // Update this method in MainWindow.xaml.cs
        public void ShowVolumeOverlay()
        {
            Debug.WriteLine("[Overlay] ShowVolumeOverlay triggered");

            if (_appSettings is null || !_appSettings.OverlayEnabled)
            {
                Debug.WriteLine("[Overlay] Disabled in settings");
                return;
            }

            if (_overlay == null)
            {
                Debug.WriteLine("[Overlay] Creating new overlay");
                Debug.WriteLine($"[Overlay] Loaded position from settings: ({_appSettings.OverlayX}, {_appSettings.OverlayY})");

                // Validate position against virtual screen bounds (multi-monitor)
                if (!IsPositionValid(_appSettings.OverlayX, _appSettings.OverlayY))
                {
                    Debug.WriteLine($"[Overlay] Loaded position ({_appSettings.OverlayX}, {_appSettings.OverlayY}) is invalid, using default");
                    _appSettings.OverlayX = 100;
                    _appSettings.OverlayY = 100;
                }

                _overlay = new FloatingOverlay(_appSettings, this);
                Debug.WriteLine($"[Overlay] Created at validated position ({_appSettings.OverlayX}, {_appSettings.OverlayY})");
            }

            var volumes = _channelControls.Select(c => c.CurrentVolume).ToList();
            var labels = _channelControls.Select(c => GetChannelLabel(c)).ToList();

            _overlay.ShowVolumes(volumes, labels);
        }

        public void UpdateOverlayPosition(double x, double y)
        {
            if (_appSettings != null)
            {
                // Store precise position with higher precision
                _appSettings.OverlayX = Math.Round(x, 1);
                _appSettings.OverlayY = Math.Round(y, 1);

                Debug.WriteLine($"[Overlay] Position updated: X={_appSettings.OverlayX}, Y={_appSettings.OverlayY}");

                // Debounce saves - only save 500ms after last position change
                if (_positionSaveTimer == null)
                {
                    _positionSaveTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(500)
                    };
                    _positionSaveTimer.Tick += PositionSaveTimer_Tick;
                }

                // Reset the timer - this cancels any pending save and starts a new 500ms countdown
                _positionSaveTimer.Stop();
                _positionSaveTimer.Start();
            }
        }

        public void UpdateOverlaySettings(AppSettings newSettings)
        {
            // Preserve current position if overlay exists and is visible
            if (_overlay != null && _overlay.IsVisible)
            {
                var currentX = Math.Round(_overlay.Left, 1);
                var currentY = Math.Round(_overlay.Top, 1);

                _appSettings.OverlayX = currentX;
                _appSettings.OverlayY = currentY;
            }
            //else if (newSettings.OverlayX > 0 && newSettings.OverlayY > 0)
            //{
            //    _appSettings.OverlayX = newSettings.OverlayX;
            //    _appSettings.OverlayY = newSettings.OverlayY;
            //}
            else if (IsPositionValid(newSettings.OverlayX, newSettings.OverlayY))
            {
                _appSettings.OverlayX = newSettings.OverlayX;
                _appSettings.OverlayY = newSettings.OverlayY;
            }
            else
            {
                _appSettings.OverlayX = 100;
                _appSettings.OverlayY = 100;
            }

            // Update all settings including text color
            _appSettings.OverlayEnabled = newSettings.OverlayEnabled;
            _appSettings.OverlayOpacity = newSettings.OverlayOpacity;
            _appSettings.OverlayTimeoutSeconds = newSettings.OverlayTimeoutSeconds;
            _appSettings.OverlayTextColor = newSettings.OverlayTextColor; // Updated property name

            Debug.WriteLine($"[Overlay] Settings updated - Text Color: {_appSettings.OverlayTextColor}");
        }

        #endregion Public Methods

        #region Protected Methods

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;

            // Stop all timers first
            _meterTimer?.Stop();
            _sessionCacheTimer?.Stop();
            _forceCleanupTimer?.Stop();
            _serialWatchdogTimer?.Stop();
            _serialReconnectTimer?.Stop();
            // we need to close all windows before we clean up the serial port and COM objects
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

            // Session tracking is now handled by the decoupled handlers

            // Clean up serial port
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.ErrorReceived -= SerialPort_ErrorReceived;

                    if (_serialPort.IsOpen)
                    {
                        _serialPort.DiscardInBuffer();
                        _serialPort.DiscardOutBuffer();
                        _serialPort.Close();
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Closing serial port: {ex.Message}");
            }

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

            // Clean up input device cache
            _inputDeviceMap.Clear();

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

        private void ApplyInputDeviceVolume(string deviceName, float level, bool isMuted)
        {
            if (!_inputDeviceMap.TryGetValue(deviceName.ToLowerInvariant(), out var mic))
            {
                mic = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

                if (mic != null)
                {
                    _inputDeviceMap[deviceName.ToLowerInvariant()] = mic;
                }
            }

            if (mic != null)
            {
                float previous = _lastInputVolume.TryGetValue(deviceName, out var lastVol) ? lastVol : -1f;

                if (Math.Abs(previous - level) > 0.01f)
                {
                    mic.AudioEndpointVolume.Mute = isMuted || level <= 0.01f;
                    mic.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                    _lastInputVolume[deviceName] = level;
                }
            }
        }

      

        private void ApplyOutputDeviceVolume(string deviceName, float level, bool isMuted)
        {
            if (!_outputDeviceMap.TryGetValue(deviceName.ToLowerInvariant(), out var spkr))
            {
                spkr = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

                if (spkr != null)
                {
                    _outputDeviceMap[deviceName.ToLowerInvariant()] = spkr;
                }
            }

            if (spkr != null)
            {
                try
                {
                    spkr.AudioEndpointVolume.Mute = isMuted;
                    spkr.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Setting output device volume for '{deviceName}': {ex.Message}");
                    _outputDeviceMap.Remove(deviceName.ToLowerInvariant());
                }
            }
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
                // Fallback to default theme if there's an error
                ApplyThemeFallback("Light");
            }
        }

        private void ApplyThemeFallback(string theme)
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
                        ApplyInputDeviceVolume(target.Name, level, ctrl.IsMuted);
                    }
                    else if (target.IsOutputDevice)
                    {
                        ApplyOutputDeviceVolume(target.Name, level, ctrl.IsMuted);
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
                                // continue; // Skip this update entirely
                                // removed as this makes fast movements unresponsive
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
                else
                {
                }
            });
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

        private void BuildOutputDeviceCache()
        {
            try
            {
                _outputDeviceMap.Clear();
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var d in devices)
                {
                    var key = d.FriendlyName.Trim().ToLowerInvariant();
                    if (!_outputDeviceMap.ContainsKey(key))
                    {
                        _outputDeviceMap[key] = d;
                    }
                }

                Debug.WriteLine($"[Init] Cached {_outputDeviceMap.Count} output devices.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Init] Failed to build output device cache: {ex.Message}");
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
                        // Test if the handler is still valid by checking the target
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


        private void CleanupInputDeviceCache()
        {
            try
            {
                // Rebuild input device cache from scratch
                _inputDeviceMap.Clear();
                BuildInputDeviceCache();
                Debug.WriteLine("[Cleanup] Rebuilt input device cache");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cleanup] Error rebuilding input device cache: {ex.Message}");
            }
        }

        private void CleanupProcessCache()
        {
            try
            {
                // More aggressive - keep only last 30 processes instead of 50
                if (_processNameCache.Count > 30)
                {
                    var oldestKeys = _processNameCache.Keys.Take(_processNameCache.Count - 20).ToList();
                    foreach (var key in oldestKeys)
                    {
                        _processNameCache.Remove(key);
                    }
                }

                Debug.WriteLine($"[Cleanup] Process cache now has {_processNameCache.Count} entries");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cleanup] Error in process cache cleanup: {ex.Message}");
            }
        }

        private void CleanupSessionCacheAggressively()
        {
            try
            {
                // Reduce max sessions from 25 to 15
                const int MAX_SESSIONS = 15;

                if (_sessionIdCache.Count > MAX_SESSIONS)
                {
                    var sessionsToRemove = _sessionIdCache.Take(_sessionIdCache.Count - MAX_SESSIONS).ToList();

                    foreach (var sessionTuple in sessionsToRemove)
                    {
                        try
                        {
                            ReleaseComObject(sessionTuple.session);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Cleanup] Failed to release COM object: {ex.Message}");
                        }
                    }

                    _sessionIdCache = _sessionIdCache.Skip(_sessionIdCache.Count - MAX_SESSIONS).ToList();
                    Debug.WriteLine($"[Cleanup] Reduced session cache to {_sessionIdCache.Count} items");
                }

                // Also clean up invalid sessions
                var validSessions = new List<(AudioSessionControl session, string sessionId, string instanceId)>();
                foreach (var sessionTuple in _sessionIdCache)
                {
                    try
                    {
                        if (sessionTuple.session != null)
                        {
                            // Test if session is still valid
                            var test = sessionTuple.session.GetProcessID;
                            validSessions.Add(sessionTuple);
                        }
                    }
                    catch
                    {
                        // Session is invalid, release it
                        try
                        {
                            if (sessionTuple.session != null)
                                ReleaseComObject(sessionTuple.session);
                        }
                        catch { }
                    }
                }

                _sessionIdCache = validSessions;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cleanup] Error in session cache cleanup: {ex.Message}");
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
                _userSelectedPort = selectedPort;
                Debug.WriteLine($"[UI] User selected port: {selectedPort}");

                // If user manually disconnected and now selects a port, clear the manual disconnect flag
                if (_manualDisconnect)
                {
                    Debug.WriteLine("[UI] User selected new port after manual disconnect - clearing manual disconnect flag");
                    _manualDisconnect = false;
                }
            }
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected && !_serialDisconnected)
            {
                // User wants to disconnect manually
                ManualDisconnect();
            }
            else
            {
                // User wants to connect to selected port
                if (ComPortSelector.SelectedItem is string selectedPort)
                {
                    Debug.WriteLine($"[Manual] User clicked connect for port: {selectedPort}");

                    // Store user's port choice
                    _userSelectedPort = selectedPort;
                    _manualDisconnect = false; // Clear manual disconnect flag

                    // Update button state immediately
                    ConnectButton.IsEnabled = false;
                    ConnectButton.Content = "Connecting...";

                    // Stop automatic reconnection while user is manually connecting
                    if (_serialReconnectTimer.IsEnabled)
                    {
                        _serialReconnectTimer.Stop();
                        Debug.WriteLine("[Manual] Stopped auto-reconnect for manual connection");
                    }

                    // Try connection
                    InitSerial(selectedPort, 9600);

                    // Re-enable auto-reconnect only if connection succeeded
                    if (_isConnected && !_serialDisconnected)
                    {
                        _serialReconnectTimer.Start();
                    }

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

                contextMenu.Items.Add(new Separator()); // Separator before exit
                contextMenu.Items.Add(exitMenuItem);

                MyNotifyIcon.ContextMenu = contextMenu;
            }
            catch (Exception ex)
            {
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

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.Close();
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                }

                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;

                UpdateConnectionStatus();

                Debug.WriteLine("[Manual] User disconnected serial port");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to disconnect: {ex.Message}");
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
                    "Jimmy White",  // ← Publisher name
                    "DeejNG",       // ← Product name
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

        private AudioSessionControl? FindSessionOptimized(SessionCollection sessions, string targetName)
        {
            try
            {
                // Limit search to prevent hanging but check enough to find the session
                int maxSessions = Math.Min(sessions.Count, 20); // Increased from 10

                for (int i = 0; i < maxSessions; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int pid = (int)session.GetProcessID;
                        if (pid <= 4) continue;

                        if (_processNameCache.TryGetValue(pid, out string procName))
                        {
                            if (procName == targetName) return session;
                        }
                        else
                        {
                            try
                            {
                                using (var process = Process.GetProcessById(pid))
                                {
                                    if (process?.ProcessName?.ToLowerInvariant() == targetName)
                                    {
                                        _processNameCache[pid] = targetName;
                                        return session;
                                    }
                                }
                            }
                            catch
                            {
                                _processNameCache[pid] = "";
                            }
                        }
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

        private void ForceCleanupTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing) return;

            try
            {
                Debug.WriteLine("[ForceCleanup] Starting aggressive cleanup...");
                _audioService?.ForceCleanup();
                AudioUtilities.ForceCleanup();

                // More frequent and aggressive cleanup
                CleanupSessionCacheAggressively();
                CleanupProcessCache();
                CleanupInputDeviceCache();
                CleanupEventHandlers();

                // IMPROVED: Reset serial communication state periodically to prevent corruption
                if ((DateTime.Now - _lastForcedCleanup).TotalMinutes > 30) // Every 30 minutes
                {
                    Debug.WriteLine("[ForceCleanup] Performing periodic state reset...");
                    PerformPeriodicStateReset();
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

            var savedSettings = LoadSettingsFromDisk();
            var savedTargetGroups = savedSettings?.SliderTargets ?? new List<List<AudioTarget>>();
            var savedInputModes = savedSettings?.InputModes ?? new List<bool>();

            _isInitializing = true;
            _allowVolumeApplication = false; // Disable ALL volume operations until first data

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

                if (i < savedInputModes.Count)
                {
                    // control.InputModeCheckBox.IsChecked = savedInputModes[i];
                }

                control.SetMuted(false);
                // DON'T set initial volume - let hardware data set it

                control.TargetChanged += (_, _) => SaveSettings();

                // VolumeOrMuteChanged will check _allowVolumeApplication flag
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

            // DON'T start meters or sync mute states until first data received
            Debug.WriteLine("[Init] Sliders generated, waiting for first hardware data before completing initialization");
        }

        //private float GetUnmappedApplicationsPeakLevel(HashSet<string> mappedApplications)
        //{
        //    // Minimal caching for maximum responsiveness
        //    if ((DateTime.Now - _lastUnmappedPeakCalculation).TotalMilliseconds < 25) // Very minimal caching
        //    {
        //        return _cachedUnmappedPeak;
        //    }

        //    float highestPeak = 0;

        //    try
        //    {
        //        var sessions = _cachedSessionsForMeters;
        //        if (sessions == null) return 0;

        //        // Process more sessions for better responsiveness
        //        int maxSessions = Math.Min(sessions.Count, 15); // Process more sessions for completeness

        //        for (int i = 0; i < maxSessions; i++)
        //        {
        //            var session = sessions[i];
        //            try
        //            {
        //                if (session == null) continue;

        //                int pid = (int)session.GetProcessID;

        //                // Skip system sessions and low PIDs
        //                if (pid <= 4) continue;

        //                string processName = AudioUtilities.GetProcessNameSafely(pid);
        //                _processNameCache[pid] = processName;

        //                // Skip if we couldn't get a valid process name
        //                if (string.IsNullOrEmpty(processName)) continue;

        //                // Skip if this application is mapped to a slider
        //                if (mappedApplications.Contains(processName)) continue;

        //                // Get the peak level for this unmapped session
        //                try
        //                {
        //                    float peak = session.AudioMeterInformation.MasterPeakValue;
        //                    if (peak > highestPeak)
        //                        highestPeak = peak;
        //                }
        //                catch
        //                {
        //                    // Session became invalid - ignore
        //                }
        //            }
        //            catch
        //            {
        //                continue;
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"[ERROR] Getting unmapped peak levels: {ex.Message}");
        //    }

        //    _cachedUnmappedPeak = highestPeak;
        //    _lastUnmappedPeakCalculation = DateTime.Now;

        //    return highestPeak;
        //}

        // Optimized unmapped peak level detection
        private float GetUnmappedApplicationsPeakLevelOptimized(HashSet<string> mappedApplications, SessionCollection sessions)
        {
            float highestPeak = 0;

            try
            {
                // Check fewer sessions but still get representative data
                int maxSessions = Math.Min(sessions.Count, 15);

                for (int i = 0; i < maxSessions; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int pid = (int)session.GetProcessID;
                        if (pid <= 4) continue;

                        // Use centralized method for consistency
                        string processName = AudioUtilities.GetProcessNameSafely(pid);

                        if (string.IsNullOrEmpty(processName))
                        {
                            continue;
                        }

                        // Check if this process is in mapped applications
                        if (mappedApplications.Contains(processName))
                        {
                            continue;
                        }

                        try
                        {
                            float peak = session.AudioMeterInformation.MasterPeakValue;
                            if (peak > 0.01f) // Only count meaningful audio levels
                            {
                                if (peak > highestPeak)
                                {
                                    highestPeak = peak;
                                }
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

        // Method to handle serial disconnection
        private void HandleSerialDisconnection()
        {
            if (_serialDisconnected) return;

            Debug.WriteLine("[Serial] Disconnection detected");
            _serialDisconnected = true;
            _isConnected = false;
            _allowVolumeApplication = false;

            // Only start auto-reconnect if this wasn't a manual disconnect
            if (!_manualDisconnect)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ConnectionStatus.Text = "Disconnected - Reconnecting...";
                    ConnectionStatus.Foreground = Brushes.Red;
                    ConnectButton.IsEnabled = true;
                    ConnectButton.Content = "Connect";
                });

                if (!_serialReconnectTimer.IsEnabled)
                {
                    _serialReconnectTimer.Start();
                }
            }
            else
            {
                // Manual disconnect - don't auto-reconnect, just update UI
                Dispatcher.BeginInvoke(() =>
                {
                    ConnectionStatus.Text = "Disconnected";
                    ConnectionStatus.Foreground = Brushes.Red;
                    ConnectButton.IsEnabled = true;
                    ConnectButton.Content = "Connect";
                });
            }

            // Clean up the serial port
            try
            {
                if (_serialPort != null)
                {
                    if (_serialPort.IsOpen)
                        _serialPort.Close();

                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to cleanup serial port: {ex.Message}");
            }
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
                    return; // Skip empty data
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
                            //  var currentInputModes = _channelControls.Select(c => c.InputModeCheckBox.IsChecked ?? false).ToList();

                            _expectedSliderCount = parts.Length;
                            GenerateSliders(parts.Length);

                            // Restore saved targets for sliders that still exist
                            for (int i = 0; i < Math.Min(currentTargets.Count, _channelControls.Count); i++)
                            {
                                _channelControls[i].AudioTargets = currentTargets[i];
                                //   _channelControls[i].InputModeCheckBox.IsChecked = currentInputModes[i];
                            }

                            // Save the new configuration
                            SaveSettings();
                            return;
                        }

                        // Process the data for existing sliders only
                        int maxIndex = Math.Min(parts.Length, _channelControls.Count);

                        for (int i = 0; i < maxIndex; i++)
                        {
                            if (!float.TryParse(parts[i].Trim(), out float level)) continue;

                            // Handle hardware noise - snap small values to exact zero
                            if (level <= 10) level = 0; // Hardware values 0-10 become exact 0
                            if (level >= 1013) level = 1023; // Hardware values 1013-1023 become exact max

                            level = Math.Clamp(level / 1023f, 0f, 1f);
                            if (InvertSliderCheckBox.IsChecked ?? false)
                                level = 1f - level;

                            var ctrl = _channelControls[i];
                            var targets = ctrl.AudioTargets;

                            if (targets.Count == 0) continue;

                            float currentVolume = ctrl.CurrentVolume;
                            if (Math.Abs(currentVolume - level) < 0.01f) continue;

                            // Always suppress events until we're ready
                            ctrl.SmoothAndSetVolume(level, suppressEvent: !_allowVolumeApplication, disableSmoothing: _disableSmoothing);

                            // Apply volume to all targets for this control ONLY if allowed
                            if (_allowVolumeApplication)
                            {
                                ApplyVolumeToTargets(ctrl, targets, level);
                                ShowVolumeOverlay();
                            }
                        }

                        // Enable volume application and do full setup after first successful data processing
                        if (!_allowVolumeApplication)
                        {
                            _allowVolumeApplication = true;
                            _isInitializing = false;

                            Debug.WriteLine("[Init] First data received - enabling volume application and completing setup");

                            // NOW it's safe to do the full initialization
                            SyncMuteStates();

                            if (!_meterTimer.IsEnabled)
                            {
                                _meterTimer.Start();
                            }
                        }

                        // Mark serial port as fully initialized after receiving valid data
                        if (!_serialPortFullyInitialized)
                        {
                            _serialPortFullyInitialized = true;
                            Debug.WriteLine("[Serial] Port fully initialized and receiving data");
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

        private void InitSerial(string portName, int baudRate)
        {
            try
            {
                // Validate port name
                if (string.IsNullOrWhiteSpace(portName))
                {
                    Debug.WriteLine("[Serial] Invalid port name provided");
                    return;
                }

                var availablePorts = SerialPort.GetPortNames();
                if (!availablePorts.Contains(portName))
                {
                    Debug.WriteLine($"[Serial] Port {portName} not in available ports: [{string.Join(", ", availablePorts)}]");

                    // Update UI to show disconnected state
                    Dispatcher.Invoke(() =>
                    {
                        _isConnected = false;
                        _serialDisconnected = true;
                        UpdateConnectionStatus();
                    });
                    return;
                }

                // Close existing connection if any
                if (_serialPort != null)
                {
                    try
                    {
                        _serialPort.DataReceived -= SerialPort_DataReceived;
                        _serialPort.ErrorReceived -= SerialPort_ErrorReceived;

                        if (_serialPort.IsOpen)
                        {
                            _serialPort.DiscardInBuffer();
                            _serialPort.DiscardOutBuffer();
                            _serialPort.Close();
                        }
                        _serialPort.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Serial] Error closing existing port: {ex.Message}");
                    }
                }

                _serialPort = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    ReceivedBytesThreshold = 1,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.ErrorReceived += SerialPort_ErrorReceived;

                _serialPort.Open();

                // Update connection state
                _isConnected = true;
                _lastConnectedPort = portName;
                _serialDisconnected = false;
                _serialPortFullyInitialized = false;

                // Reset volume application flag on new connection
                _allowVolumeApplication = false;

                // Reset the watchdog variables
                _lastValidDataTimestamp = DateTime.Now;
                _noDataCounter = 0;
                _expectingData = false;

                // Make sure the watchdog is running
                if (!_serialWatchdogTimer.IsEnabled)
                {
                    _serialWatchdogTimer.Start();
                }

                // Update UI
                Dispatcher.Invoke(() =>
                {
                    UpdateConnectionStatus();
                    // Ensure ComboBox shows the connected port
                    ComPortSelector.SelectedItem = portName;
                });

                // Start the reconnect timer
                if (!_serialReconnectTimer.IsEnabled)
                {
                    _serialReconnectTimer.Start();
                }

                Debug.WriteLine($"[Serial] Successfully connected to {portName} - waiting for data before applying ANY volumes");

                // Save settings only if we've loaded initial settings
                if (_hasLoadedInitialSettings)
                {
                    SaveSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Serial] Failed to open port {portName}: {ex.Message}");

                // Update connection state
                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;

                Dispatcher.Invoke(() =>
                {
                    UpdateConnectionStatus();
                });

                // Don't show error dialog during automatic connection attempts
                if (_hasLoadedInitialSettings)
                {
                    // Only show error if this was a manual connection attempt
                    var isManualAttempt = ComPortSelector.SelectedItem?.ToString() == portName;
                    if (isManualAttempt)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show($"Failed to open serial port {portName}: {ex.Message}",
                                          "Serial Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
                }
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

        private bool IsPositionValid(double x, double y)
        {
            // Use virtual screen bounds to support multi-monitor setups
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            var virtualRight = virtualLeft + virtualWidth;
            var virtualBottom = virtualTop + virtualHeight;

            // Allow position anywhere within virtual screen bounds (with small margin)
            bool isValid = x >= virtualLeft - 100 &&
                           x <= virtualRight - 100 &&
                           y >= virtualTop - 50 &&
                           y <= virtualBottom - 50;

            Debug.WriteLine($"[Position] Checking position ({x}, {y}) against virtual bounds ({virtualLeft}, {virtualTop}) to ({virtualRight}, {virtualBottom}) - Valid: {isValid}");

            return isValid;
        }

        private bool IsStartupEnabled()
        {
            const string appName = "DeejNG";
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            var value = key?.GetValue(appName) as string;
            return !string.IsNullOrEmpty(value);
        }

        private void LoadAvailablePorts()
        {
            try
            {
                var availablePorts = SerialPort.GetPortNames();

                // Store current selection if any
                string currentSelection = ComPortSelector.SelectedItem as string;

                // Update the ComboBox
                ComPortSelector.ItemsSource = availablePorts;

                Debug.WriteLine($"[Ports] Found {availablePorts.Length} ports: [{string.Join(", ", availablePorts)}]");
                Debug.WriteLine($"[Ports] Current selection: '{currentSelection}', Last connected port: '{_lastConnectedPort}'");

                // Try to restore previous selection first
                if (!string.IsNullOrEmpty(currentSelection) && availablePorts.Contains(currentSelection))
                {
                    ComPortSelector.SelectedItem = currentSelection;
                    Debug.WriteLine($"[Ports] Restored previous selection: {currentSelection}");
                }
                // Then try saved port from settings
                else if (!string.IsNullOrEmpty(_lastConnectedPort) && availablePorts.Contains(_lastConnectedPort))
                {
                    ComPortSelector.SelectedItem = _lastConnectedPort;
                    Debug.WriteLine($"[Ports] Selected saved port: {_lastConnectedPort}");
                }
                // Finally, select first available port
                else if (availablePorts.Length > 0)
                {
                    ComPortSelector.SelectedIndex = 0;
                    Debug.WriteLine($"[Ports] No saved port found or available, selected first available port: {availablePorts[0]}");
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
                var settings = LoadSettingsFromDisk();
                if (!string.IsNullOrWhiteSpace(settings?.PortName))
                {
                    _lastConnectedPort = settings.PortName;
                    Debug.WriteLine($"[Settings] Loaded saved port name: {_lastConnectedPort}");
                }
                else
                {
                    Debug.WriteLine("[Settings] No saved port name found");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to load saved port name: {ex.Message}");
            }
        }

        //private void LoadSettings()
        //{
        //    var settings = LoadSettingsFromDisk();
        //    if (!string.IsNullOrWhiteSpace(settings?.PortName))
        //    {
        //        InitSerial(settings.PortName, 9600);
        //    }

        //    ApplyTheme(settings?.IsDarkTheme == true ? "Dark" : "Light");
        //    InvertSliderCheckBox.IsChecked = settings?.IsSliderInverted ?? false;
        //    ShowSlidersCheckBox.IsChecked = settings?.VuMeters ?? true;

        //    bool showMeters = settings?.VuMeters ?? true;
        //    ShowSlidersCheckBox.IsChecked = showMeters;
        //    SetMeterVisibilityForAll(showMeters);
        //    DisableSmoothingCheckBox.IsChecked = settings?.DisableSmoothing ?? false;

        //    // ✅ Unsubscribe events temporarily
        //    StartOnBootCheckBox.Checked -= StartOnBootCheckBox_Checked;
        //    StartOnBootCheckBox.Unchecked -= StartOnBootCheckBox_Unchecked;

        //    bool isInStartup = IsStartupEnabled();
        //    _appSettings.StartOnBoot = isInStartup;
        //    StartOnBootCheckBox.IsChecked = isInStartup;

        //    // ✅ Re-subscribe after setting the value
        //    StartOnBootCheckBox.Checked += StartOnBootCheckBox_Checked;
        //    StartOnBootCheckBox.Unchecked += StartOnBootCheckBox_Unchecked;

        //    StartMinimizedCheckBox.IsChecked = settings?.StartMinimized ?? false;
        //    StartMinimizedCheckBox.Checked += StartMinimizedCheckBox_Checked;
        //    foreach (var ctrl in _channelControls)
        //        ctrl.SetMeterVisibility(showMeters);
        //}

        // Update LoadSettingsFromDisk to add debugging
        private AppSettings LoadSettingsFromDisk()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    Debug.WriteLine($"[Settings] Loading from: {SettingsPath}");

                    _appSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayEnabled: {_appSettings.OverlayEnabled}");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayPosition: ({_appSettings.OverlayX}, {_appSettings.OverlayY})");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayOpacity: {_appSettings.OverlayOpacity}");
                    Debug.WriteLine($"[Settings] Loaded from disk - OverlayTimeoutSeconds: {_appSettings.OverlayTimeoutSeconds}");

                    return _appSettings;
                }
                else
                {
                    Debug.WriteLine($"[Settings] Settings file does not exist: {SettingsPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Error loading settings: {ex.Message}");
            }

            _appSettings = new AppSettings();
            Debug.WriteLine("[Settings] Using default AppSettings");
            return _appSettings;
        }
        private void LoadSettingsWithoutSerialConnection()
        {
            try
            {
                var settings = LoadSettingsFromDisk();
                _appSettings = settings ?? new AppSettings();

                // Apply UI settings
                ApplyTheme(_appSettings.IsDarkTheme ? "Dark" : "Light");
                InvertSliderCheckBox.IsChecked = _appSettings.IsSliderInverted;
                ShowSlidersCheckBox.IsChecked = _appSettings.VuMeters;

                SetMeterVisibilityForAll(_appSettings.VuMeters);
                DisableSmoothingCheckBox.IsChecked = _appSettings.DisableSmoothing;

                // Validate overlay position using virtual screen bounds (multi-monitor support)
                Debug.WriteLine($"[Settings] Initial overlay position from file: ({_appSettings.OverlayX}, {_appSettings.OverlayY})");

                if (!IsPositionValid(_appSettings.OverlayX, _appSettings.OverlayY))
                {
                    Debug.WriteLine($"[Settings] Position ({_appSettings.OverlayX}, {_appSettings.OverlayY}) is outside virtual screen bounds, resetting to default");
                    _appSettings.OverlayX = 100;
                    _appSettings.OverlayY = 100;
                }
                else
                {
                    Debug.WriteLine($"[Settings] Position ({_appSettings.OverlayX}, {_appSettings.OverlayY}) is valid for multi-monitor setup");
                }

                if (_appSettings.OverlayOpacity <= 0 || _appSettings.OverlayOpacity > 1)
                {
                    _appSettings.OverlayOpacity = 0.85;
                }

                Debug.WriteLine($"[Settings] Final overlay settings - Enabled: {_appSettings.OverlayEnabled}, Position: ({_appSettings.OverlayX}, {_appSettings.OverlayY}), Opacity: {_appSettings.OverlayOpacity}, Timeout: {_appSettings.OverlayTimeoutSeconds}");

                // Handle startup settings
                StartOnBootCheckBox.Checked -= StartOnBootCheckBox_Checked;
                StartOnBootCheckBox.Unchecked -= StartOnBootCheckBox_Unchecked;

                bool isInStartup = IsStartupEnabled();
                _appSettings.StartOnBoot = isInStartup;
                StartOnBootCheckBox.IsChecked = isInStartup;

                StartOnBootCheckBox.Checked += StartOnBootCheckBox_Checked;
                StartOnBootCheckBox.Unchecked += StartOnBootCheckBox_Unchecked;

                StartMinimizedCheckBox.IsChecked = _appSettings.StartMinimized;
                StartMinimizedCheckBox.Checked += StartMinimizedCheckBox_Checked;

                // Generate sliders from saved settings
                if (_appSettings.SliderTargets != null && _appSettings.SliderTargets.Count > 0)
                {
                    _expectedSliderCount = _appSettings.SliderTargets.Count;
                    GenerateSliders(_appSettings.SliderTargets.Count);
                }
                else
                {
                    _expectedSliderCount = 4;
                    GenerateSliders(4);
                }

                _hasLoadedInitialSettings = true;

                foreach (var ctrl in _channelControls)
                    ctrl.SetMeterVisibility(_appSettings.VuMeters);

                // If overlay is enabled and autohide is disabled, show it immediately after startup
                if (_appSettings.OverlayEnabled && _appSettings.OverlayTimeoutSeconds == AppSettings.OverlayNoTimeout)
                {
                    // Delay slightly to ensure everything is loaded
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
            StartOnBootCheckBox.IsChecked = _appSettings.StartOnBoot;
            //  DebugAudioSessions(); // Add this line
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

        // Add this new method
        private void ManualDisconnect()
        {
            try
            {
                Debug.WriteLine("[Manual] User initiated manual disconnect");

                // Set flag to prevent automatic reconnection
                _manualDisconnect = true;

                // Stop the auto-reconnect timer
                if (_serialReconnectTimer.IsEnabled)
                {
                    _serialReconnectTimer.Stop();
                    Debug.WriteLine("[Manual] Stopped auto-reconnect timer");
                }

                // Close the serial port
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.Close();
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                }

                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;
                _allowVolumeApplication = false;

                UpdateConnectionStatus();

                Debug.WriteLine("[Manual] Manual disconnect completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to disconnect manually: {ex.Message}");
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

        private void PerformMaintenance()
        {
            // Reset connection states and sync mute states less frequently
            foreach (var control in _channelControls)
            {
                control.ResetConnectionState();
            }
            SyncMuteStates();

            // Clean up collections less frequently but more thoroughly
            CleanupSessionCacheAggressively();
            CleanupProcessCache();

            // Force a small GC if memory pressure is high
            if (GC.GetTotalMemory(false) > 50_000_000) // 50MB threshold
            {
                GC.Collect(0, GCCollectionMode.Optimized);
            }
        }

        private void PerformPeriodicStateReset()
        {
            try
            {
                // Clear and reset serial buffer
                _serialBuffer.Clear();

                // Reset volume application flags to ensure responsiveness
                if (_isConnected && !_serialDisconnected)
                {
                    _allowVolumeApplication = true;
                }

                // Refresh audio device reference
                try
                {
                    _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                catch { }

                // Force a complete mute state resync
                if (_allowVolumeApplication && _channelControls.Count > 0)
                {
                    SyncMuteStates();
                }

                Debug.WriteLine("[PeriodicReset] State reset completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PeriodicReset] Error during state reset: {ex.Message}");
            }
        }

        private void PositionSaveTimer_Tick(object sender, EventArgs e)
        {
            _positionSaveTimer.Stop();

            // Save to disk on background thread
            Task.Run(() =>
            {
                try
                {
                    var json = JsonSerializer.Serialize(_appSettings, new JsonSerializerOptions { WriteIndented = true });
                    var dir = Path.GetDirectoryName(SettingsPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllText(SettingsPath, json);
                    Debug.WriteLine("[Overlay] Position saved to disk (debounced)");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Failed to save overlay position: {ex.Message}");
                }
            });
        }
      

        private void RefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            // Refresh the list of available ports
            LoadAvailablePorts();

            // Show a message if no ports are available
            if (ComPortSelector.Items.Count == 0)
            {
                MessageBox.Show("No serial ports detected. Please connect your device and try again.",
                                "No Ports Available",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
            }
            else
            {
                // If we're in disconnected state and there are ports available,
                // attempt to reconnect to the last known port
                if (_serialDisconnected && !string.IsNullOrEmpty(_lastConnectedPort) &&
                    ComPortSelector.Items.Contains(_lastConnectedPort))
                {
                    ComPortSelector.SelectedItem = _lastConnectedPort;
                    InitSerial(_lastConnectedPort, 9600);
                }
            }
        }

        private void ReleaseComObject(object comObject)
        {
            if (comObject != null)
            {
                try
                {
                    int refCount;
                    do
                    {
                        refCount = Marshal.ReleaseComObject(comObject);
                    } while (refCount > 0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[COM] Error releasing COM object: {ex.Message}");
                }
            }
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

        private void SaveSettings()
        {
            if (_isInitializing)
            {
                Debug.WriteLine("[Settings] Skipping save during initialization");
                return;
            }
            if (!_hasLoadedInitialSettings)
            {
                Debug.WriteLine("[Settings] Skipping save - initial settings not loaded yet");
                return;
            }
            // Prevent too frequent saves
            if ((DateTime.Now - _lastSettingsSave).TotalMilliseconds < 500)
            {
                Debug.WriteLine("[Settings] Skipping save - too frequent");
                return;
            }
            lock (_settingsLock)
            {
                try
                {
                    // Validate that we have meaningful data to save
                    if (_channelControls.Count == 0)
                    {
                        Debug.WriteLine("[Settings] Skipping save - no channel controls");
                        return;
                    }

                    var settings = new AppSettings
                    {
                        PortName = _serialPort?.PortName ?? string.Empty,
                        SliderTargets = _channelControls.Select(c => c.AudioTargets ?? new List<AudioTarget>()).ToList(),
                        IsDarkTheme = isDarkTheme,
                        IsSliderInverted = InvertSliderCheckBox.IsChecked ?? false,
                        VuMeters = ShowSlidersCheckBox.IsChecked ?? true,
                        StartOnBoot = StartOnBootCheckBox.IsChecked ?? false,
                        StartMinimized = StartMinimizedCheckBox.IsChecked ?? false,
                        //  InputModes = _channelControls.Select(c => c.InputModeCheckBox.IsChecked ?? false).ToList(),
                        DisableSmoothing = DisableSmoothingCheckBox.IsChecked ?? false,

                        // ✅ PRESERVE OVERLAY SETTINGS FROM _appSettings
                        OverlayEnabled = _appSettings.OverlayEnabled,
                        OverlayOpacity = _appSettings.OverlayOpacity,
                        OverlayTimeoutSeconds = _appSettings.OverlayTimeoutSeconds,
                        OverlayX = _appSettings.OverlayX,
                        OverlayY = _appSettings.OverlayY
                    };

                    // Additional validation
                    if (settings.SliderTargets.Count != _channelControls.Count)
                    {
                        Debug.WriteLine("[Settings] Warning: Slider targets count mismatch");
                    }

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    var json = JsonSerializer.Serialize(settings, options);

                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(SettingsPath);
                    if (!Directory.Exists(dir) && dir != null)
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(SettingsPath, json);
                    _lastSettingsSave = DateTime.Now;

                    Debug.WriteLine($"[Settings] Saved successfully with {settings.SliderTargets.Count} slider configurations and overlay settings");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
                }
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                // If we're here but the port is not open, we have a disconnection
                HandleSerialDisconnection();
                return;
            }

            try
            {
                string incoming = _serialPort.ReadExisting();

                // Update timestamp when we receive data
                _lastValidDataTimestamp = DateTime.Now;
                _expectingData = true; // We're now expecting regular data
                _noDataCounter = 0;    // Reset the counter since we got data

                // IMPROVED: More aggressive buffer management to prevent long-running issues
                if (_serialBuffer.Length > 1024) // Further reduced to 1KB limit
                {
                    // Try to salvage partial data by finding the last newline
                    string bufferContent = _serialBuffer.ToString();
                    int lastNewline = Math.Max(bufferContent.LastIndexOf('\n'), bufferContent.LastIndexOf('\r'));

                    if (lastNewline > 0)
                    {
                        _serialBuffer.Clear();
                        _serialBuffer.Append(bufferContent.Substring(lastNewline + 1));
                        Debug.WriteLine("[WARNING] Serial buffer trimmed to last valid line");
                    }
                    else
                    {
                        _serialBuffer.Clear();
                        Debug.WriteLine("[WARNING] Serial buffer exceeded limit and was cleared");
                    }
                }

                // IMPROVED: Filter out non-printable characters and invalid data to prevent corruption
                incoming = _invalidSerialCharsRegex.Replace(incoming, "");
                _serialBuffer.Append(incoming);

                // Process all complete lines in the buffer
                while (true)
                {
                    string buffer = _serialBuffer.ToString();
                    int newLineIndex = buffer.IndexOf('\n');
                    if (newLineIndex == -1)
                    {
                        // Also check for carriage return in case line endings vary
                        newLineIndex = buffer.IndexOf('\r');
                        if (newLineIndex == -1) break;
                    }

                    string line = buffer.Substring(0, newLineIndex).Trim();

                    // Remove the processed line including any CR/LF characters
                    int removeLength = newLineIndex + 1;
                    if (buffer.Length > newLineIndex + 1)
                    {
                        if (buffer[newLineIndex] == '\r' && buffer[newLineIndex + 1] == '\n')
                            removeLength++;
                        else if (buffer[newLineIndex] == '\n' && buffer[newLineIndex + 1] == '\r')
                            removeLength++;
                    }

                    _serialBuffer.Remove(0, removeLength);

                    // IMPROVED: More strict validation to prevent processing corrupt data
                    if (!string.IsNullOrWhiteSpace(line) && line.Length < 200) // Reasonable max length check
                    {
                        Dispatcher.BeginInvoke(() => HandleSliderData(line));
                    }
                }

                // IMPROVED: Periodic buffer cleanup to prevent memory issues
                if (_serialBuffer.Length == 0 && DateTime.Now.Minute % 5 == 0) // Every 5 minutes
                {
                    _serialBuffer.Clear();
                }
            }
            catch (IOException)
            {
                // IO Exception can indicate device disconnection
                HandleSerialDisconnection();
            }
            catch (InvalidOperationException)
            {
                // This can happen if the port is closed while we're reading
                HandleSerialDisconnection();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Serial read: {ex.Message}");
                _serialBuffer.Clear(); // Clear the buffer on unexpected errors
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Debug.WriteLine($"[Serial] Error received: {e.EventType}");

            // Check for disconnection conditions
            if (e.EventType == SerialError.Frame || e.EventType == SerialError.RXOver ||
                e.EventType == SerialError.Overrun || e.EventType == SerialError.RXParity)
            {
                HandleSerialDisconnection();
            }
        }

        private void SerialReconnectTimer_Tick(object sender, EventArgs e)
        {
            // Don't auto-reconnect if user manually disconnected
            if (_isClosing || !_serialDisconnected || _manualDisconnect)
            {
                if (_manualDisconnect)
                {
                    Debug.WriteLine("[SerialReconnect] Skipping auto-reconnect - user disconnected manually");
                }
                return;
            }

            Debug.WriteLine("[SerialReconnect] Attempting to reconnect...");

            // Try the last connected port first
            if (TryConnectToSavedPort())
            {
                Debug.WriteLine("[SerialReconnect] Successfully reconnected to saved port");
                return;
            }

            // If saved port doesn't work, try any available port
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

                // Try the first available port
                string portToTry = availablePorts[0];
                Debug.WriteLine($"[SerialReconnect] Trying first available port: {portToTry}");

                InitSerial(portToTry, 9600);

                if (_isConnected)
                {
                    Debug.WriteLine($"[SerialReconnect] Successfully connected to {portToTry}");
                    _serialDisconnected = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialReconnect] Failed to reconnect: {ex.Message}");
            }
        }

        private void SerialWatchdogTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing || !_isConnected || _serialDisconnected) return;

            try
            {
                // First check if the port is actually open
                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    Debug.WriteLine("[SerialWatchdog] Serial port closed unexpectedly");
                    HandleSerialDisconnection();
                    return;
                }

                // Check if we're receiving data
                if (_expectingData)
                {
                    TimeSpan elapsed = DateTime.Now - _lastValidDataTimestamp;

                    // If it's been more than 5 seconds without data, assume disconnected
                    if (elapsed.TotalSeconds > 5)
                    {
                        _noDataCounter++;
                        Debug.WriteLine($"[SerialWatchdog] No data received for {elapsed.TotalSeconds:F1} seconds (count: {_noDataCounter})");

                        // After 3 consecutive timeouts, consider disconnected
                        if (_noDataCounter >= 3)
                        {
                            Debug.WriteLine("[SerialWatchdog] Too many timeouts, considering disconnected");
                            HandleSerialDisconnection();
                            _noDataCounter = 0;
                            return;
                        }

                        // Try to write a single byte to test connection
                        try
                        {
                            // A zero-length write doesn't always work, so use a simple linefeed
                            // Most Arduino-based controllers will ignore this
                            _serialPort.Write(new byte[] { 10 }, 0, 1);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SerialWatchdog] Exception when testing connection: {ex.Message}");
                            HandleSerialDisconnection();
                            return;
                        }
                    }
                    else
                    {
                        // Reset counter if we're getting data
                        _noDataCounter = 0;
                    }
                }
                else if (_isConnected && (DateTime.Now - _lastValidDataTimestamp).TotalSeconds > 10)
                {
                    // If we haven't seen any data for 10 seconds after connecting,
                    // we may need to set the flag to start expecting data
                    _expectingData = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialWatchdog] Error: {ex.Message}");
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
            settingsWindow.Show();  // Show as non-modal window (or ShowDialog() if you prefer modal)
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

                if (TryConnectToSavedPort())
                {
                    Debug.WriteLine("[AutoConnect] Successfully connected!");
                    attemptTimer.Stop();
                    return;
                }

                if (connectionAttempts >= maxAttempts)
                {
                    Debug.WriteLine($"[AutoConnect] Failed after {maxAttempts} attempts");
                    attemptTimer.Stop();
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
            _appSettings.StartMinimized = true;
            SaveSettings();
        }

        private void StartMinimizedCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _appSettings.StartMinimized = false;
            SaveSettings();
        }

        private void StartOnBootCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            EnableStartup();
            _appSettings.StartOnBoot = true;
            SaveSettings();
        }

        private void StartOnBootCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            //check the value in _settings

            DisableStartup();
            _appSettings.StartOnBoot = false;
            SaveSettings();
        }

        private void StartSessionCacheUpdater()
        {
            _sessionCacheTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(7) // Balance between performance and responsiveness
            };

            int tickCount = 0;

            _sessionCacheTimer.Tick += (_, _) =>
            {
                if (_isClosing) return;

                try
                {
                    // Batch multiple operations together for better performance
                    if (tickCount % 2 == 0) // Every other tick
                    {
                        UpdateSessionCache();
                    }

                    if (tickCount % 6 == 0) // Every 6th tick (1 minute)
                    {
                        PerformMaintenance();
                    }

                    tickCount++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Session cache update: {ex.Message}");
                }
            };

            _sessionCacheTimer.Start();
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

                // CRITICAL FIX: Unregister ALL existing handlers first to prevent accumulation
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

                // Build session map with proper error handling and PID tracking
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
                            // Check for process restarts
                            if (sessionsByProcess.TryGetValue(procName, out var existingSession) &&
                                sessionProcessIds.TryGetValue(procName, out var existingPid) &&
                                existingPid != pid)
                            {
                                Debug.WriteLine($"[Sync] Detected {procName} restart: PID {existingPid} -> {pid}");
                            }

                            sessionsByProcess[procName] = s;
                            sessionProcessIds[procName] = pid;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] Mapping session in SyncMuteStates: {ex.Message}");
                    }
                }

                // Get all unique targets across all controls
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

                // Register handlers for each unique target (not per control)
                foreach (var targetName in allTargets)
                {
                    if (targetName == "system" || targetName == "unmapped") continue;

                    if (sessionsByProcess.TryGetValue(targetName, out var matchedSession))
                    {
                        try
                        {
                            // Create ONE decoupled handler per target
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
                            if (_outputDeviceMap.TryGetValue(target.Name.ToLowerInvariant(), out var spkr))
                            {
                                bool isMuted = spkr.AudioEndpointVolume.Mute;
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
                            else
                            {
                                Debug.WriteLine($"[Sync] No active session found for {targetName}");
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
                // Clear all explicitly set foreground colors on channel controls
                foreach (var control in _channelControls)
                {
                    control.TargetTextBox.ClearValue(TextBox.ForegroundProperty);
                    control.InvalidateVisual();
                }

                // Force a complete visual refresh
                this.InvalidateVisual();
                this.UpdateLayout();
            }, DispatcherPriority.Render);

            SaveSettings();
        }

        private bool TryConnectToSavedPort()
        {
            try
            {
                if (_isConnected && !_serialDisconnected)
                {
                    return true;
                }

                // If user manually selected a port, use that instead of saved port
                string portToTry;
                if (!string.IsNullOrEmpty(_userSelectedPort))
                {
                    portToTry = _userSelectedPort;
                    Debug.WriteLine($"[AutoConnect] Using user-selected port: {portToTry}");
                }
                else
                {
                    var settings = LoadSettingsFromDisk();
                    if (string.IsNullOrWhiteSpace(settings?.PortName))
                    {
                        Debug.WriteLine("[AutoConnect] No saved port name");
                        return false;
                    }
                    portToTry = settings.PortName;
                    Debug.WriteLine($"[AutoConnect] Using saved port: {portToTry}");
                }

                LoadAvailablePorts();

                var availablePorts = SerialPort.GetPortNames();
                if (!availablePorts.Contains(portToTry))
                {
                    Debug.WriteLine($"[AutoConnect] Port '{portToTry}' not available. Available: [{string.Join(", ", availablePorts)}]");
                    return false;
                }

                // Update ComboBox to show the port we're connecting to
                Dispatcher.Invoke(() =>
                {
                    ComPortSelector.SelectedItem = portToTry;
                });

                InitSerial(portToTry, 9600);

                // Clear user selected port after successful connection
                if (_isConnected && !_serialDisconnected)
                {
                    _userSelectedPort = string.Empty;
                }

                return _isConnected && !_serialDisconnected;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoConnect] Exception: {ex.Message}");
                return false;
            }
        }
        private void UpdateConnectionStatus()
        {
            string statusText;
            Brush statusColor;

            if (_isConnected && !_serialDisconnected)
            {
                statusText = $"Connected to {_serialPort?.PortName ?? _lastConnectedPort}";
                statusColor = Brushes.Green;
            }
            else if (_serialDisconnected)
            {
                statusText = "Disconnected - Reconnecting...";
                statusColor = Brushes.Orange;
            }
            else
            {
                statusText = "Disconnected";
                statusColor = Brushes.Red;
            }

            // Update UI elements
            ConnectionStatus.Text = statusText;
            ConnectionStatus.Foreground = statusColor;

            // Always enable the button, just change the text
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = (_isConnected && !_serialDisconnected) ? "Disconnect" : "Connect";

            Debug.WriteLine($"[Status] {statusText}");
        }

      
        private void UpdateMeters(object? sender, EventArgs e)
        {
            if (!_metersEnabled || _isClosing) return;

            // Remove skip counter for maximum responsiveness - update every tick
            // if (++_meterSkipCounter % 2 != 0) return; // Commented out for responsiveness

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
                if ((DateTime.Now - _lastMeterSessionRefresh).TotalMilliseconds > 50) // Very frequent updates for responsiveness
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

                        // Process ALL targets but with optimizations
                        foreach (var target in targets)
                        {
                            try
                            {
                                if (target.IsInputDevice)
                                {
                                    if (_inputDeviceMap.TryGetValue(target.Name.ToLowerInvariant(), out var mic))
                                    {
                                        try
                                        {
                                            float peak = mic.AudioMeterInformation.MasterPeakValue;
                                            if (peak > highestPeak) highestPeak = peak;
                                            if (!mic.AudioEndpointVolume.Mute) allMuted = false;
                                        }
                                        catch (ArgumentException)
                                        {
                                            _inputDeviceMap.Remove(target.Name.ToLowerInvariant());
                                        }
                                    }
                                }
                                else if (target.IsOutputDevice)
                                {
                                    if (_outputDeviceMap.TryGetValue(target.Name.ToLowerInvariant(), out var spkr))
                                    {
                                        try
                                        {
                                            float peak = spkr.AudioMeterInformation.MasterPeakValue;
                                            if (peak > highestPeak) highestPeak = peak;
                                            if (!spkr.AudioEndpointVolume.Mute) allMuted = false;
                                        }
                                        catch (ArgumentException)
                                        {
                                            _outputDeviceMap.Remove(target.Name.ToLowerInvariant());
                                        }
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
                                    mappedApps.Remove("unmapped"); // Don't exclude unmapped from itself

                                    float unmappedPeak = GetUnmappedApplicationsPeakLevelOptimized(mappedApps, sessions);
                                    if (unmappedPeak > highestPeak)
                                    {
                                        highestPeak = unmappedPeak;
                                    }
                                    if (!ctrl.IsMuted) allMuted = false;
                                }
                                else
                                {
                                    // Find specific application with reasonable limit
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
                                        catch (ArgumentException)
                                        {
                                            // Session became invalid during access
                                            continue;
                                        }
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

            // Process in smaller batches to reduce GC pressure
            var sessions = defaultDevice.AudioSessionManager.Sessions;
            int batchSize = Math.Min(sessions.Count, 15); // Process max 15 sessions

            for (int i = 0; i < batchSize; i++)
            {
                var session = sessions[i];
                try
                {
                    if (session == null) continue;

                    int processId = (int)session.GetProcessID;
                    string processName = AudioUtilities.GetProcessNameSafely(processId);
                    _processNameCache[processId] = processName;

                    if (!string.IsNullOrEmpty(processName))
                    {
                        // Update cache without creating unnecessary objects
                        UpdateSessionCacheEntry(session, processName, processId);
                    }
                }
                catch (ArgumentException)
                {
                    continue; // Session no longer valid
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Processing session in cache updater: {ex.Message}");
                    continue;
                }
            }
        }
        private void UpdateSessionCacheEntry(AudioSessionControl session, string processName, int processId)
        {
            try
            {
                string sessionId = session.GetSessionIdentifier?.ToLowerInvariant() ?? "";
                string instanceId = session.GetSessionInstanceIdentifier?.ToLowerInvariant() ?? "";

                // Update cache only if needed
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