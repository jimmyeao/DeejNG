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

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        #region Private Fields
        private DispatcherTimer _sessionCacheTimer;
        private bool _isClosing = false;
        private readonly Dictionary<string, float> _lastInputVolume = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, IAudioSessionEventsHandler> _registeredHandlers = new();
        private AudioService _audioService;
        private bool _metersEnabled = true;

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
            _sessionCacheTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };

            int tickCount = 0;

            _sessionCacheTimer.Tick += (_, _) =>
            {
                if (_isClosing) return;

                try
                {
                    var defaultDevice = new MMDeviceEnumerator()
                        .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    // Refresh the audio device reference
                    _audioDevice = defaultDevice;

                    var sessions = defaultDevice.AudioSessionManager.Sessions;
                    var activeSessions = new Dictionary<string, AudioSessionControl>();
                    var activeProcesses = new Dictionary<int, string>();

                    // First, get all current sessions and processes
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            int processId = (int)session.GetProcessID;
                            string processName = "";

                            // Try to get process name from cache or lookup
                            if (!_processNameCache.TryGetValue(processId, out processName))
                            {
                                try
                                {
                                    var process = Process.GetProcessById(processId);
                                    processName = Path.GetFileNameWithoutExtension(process.MainModule.FileName)
                                                .ToLowerInvariant();
                                }
                                catch
                                {
                                    // Process may have exited or be inaccessible
                                    processName = $"pid_{processId}";
                                }

                                // Cache the result
                                _processNameCache[processId] = processName;
                            }

                            // Add to active processes map
                            activeProcesses[processId] = processName;

                            // Get the identifiers for the session
                            string sessionId = session.GetSessionIdentifier?.ToLowerInvariant() ?? "";
                            string instanceId = session.GetSessionInstanceIdentifier?.ToLowerInvariant() ?? "";

                            // Store in active sessions map
                            string sessionKey = $"{processName}_{instanceId}";
                            activeSessions[sessionKey] = session;

                            // Check if this is a new session we haven't seen before
                            if (!_sessionIdCache.Any(t => t.sessionId == sessionId && t.instanceId == instanceId))
                            {
                                _sessionIdCache.Add((session, sessionId, instanceId));
                                Debug.WriteLine($"[SessionCache] Found new session: {processName} ({processId})");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ERROR] Processing session in cache updater: {ex.Message}");
                        }
                    }

                    // Clean up stale entries from session and process caches
                    var pidsToRemove = _processNameCache.Keys
                        .Where(pid => !activeProcesses.ContainsKey(pid))
                        .ToList();

                    foreach (var pid in pidsToRemove)
                    {
                        _processNameCache.Remove(pid);
                    }

                    // Check for session reconnections every 15 seconds
                    if (++tickCount % 3 == 0)
                    {
                        foreach (var control in _channelControls)
                        {
                            // Reset connection state visuals in case the app has reconnected
                            control.ResetConnectionState();
                        }

                        // Resync mute states
                        SyncMuteStates();
                    }

                    // Run full cleanup every minute (12 ticks at 5-second intervals)
                    if (tickCount % 12 == 0)
                    {
                        CleanupDeadSessions();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Session cache update: {ex.Message}");
                }
            };

            _sessionCacheTimer.Start();
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
            _isClosing = true;

            // Stop all timers
            _meterTimer?.Stop();
            _sessionCacheTimer?.Stop();

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

            // Existing cleanup code
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

                // Unsubscribe from all events
                if (_systemVolume != null)
                {
                    _systemVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                }

                _isConnected = false;
                UpdateConnectionStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while closing serial port: {ex.Message}", "Cleanup Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            foreach (var sessionTuple in _sessionIdCache)
            {
                try
                {
                    if (sessionTuple.session != null)
                        Marshal.ReleaseComObject(sessionTuple.session);
                }
                catch { }
            }
            _sessionIdCache.Clear();

            base.OnClosed(e);
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
            var savedTargetGroups = savedSettings?.SliderTargets ?? new List<List<AudioTarget>>();
            var savedInputModes = savedSettings?.InputModes ?? new List<bool>();

            _isInitializing = true; // ensure this is explicitly set at start

            for (int i = 0; i < count; i++)
            {
                var control = new ChannelControl();

                // Set targets for this control (either from saved settings or defaults)
                List<AudioTarget> targetsForThisControl;

                if (i < savedTargetGroups.Count && savedTargetGroups[i].Count > 0)
                {
                    // Use saved targets
                    targetsForThisControl = savedTargetGroups[i];
                }
                else
                {
                    // Create default targets
                    targetsForThisControl = new List<AudioTarget>();

                    // Add system target for first slider by default
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

                // Set input mode from saved settings if available
                // This needs to happen after setting AudioTargets, as that can override IsInputMode
                if (i < savedInputModes.Count)
                {
                    // If we have a saved input mode, set it directly to the checkbox
                    control.InputModeCheckBox.IsChecked = savedInputModes[i];
                }

                control.SetMuted(false);
                control.SetVolume(0.5f);

                control.TargetChanged += (_, _) => SaveSettings();
                control.VolumeOrMuteChanged += (targets, vol, mute) =>
                {
                    if (_isInitializing) return;
                    ApplyVolumeToTargets(control, targets, vol);
                };

                // Add handler for session disconnection
                control.SessionDisconnected += (sender, target) =>
                {
                    Debug.WriteLine($"[MainWindow] Received session disconnected for {target}");

                    // Optional: You could try to reconnect to the session here
                    // For now, we'll just update visual state and remove from registeredHandlers
                    if (_registeredHandlers.TryGetValue(target, out var handler))
                    {
                        _registeredHandlers.Remove(target);
                        Debug.WriteLine($"[MainWindow] Removed handler for disconnected session: {target}");
                    }

                    // Next time the session cache updates, it will try to find this session again
                    // if the application is still running
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
            try
            {
                var audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = audioDevice.AudioSessionManager.Sessions;

                // First, unregister old handlers that aren't needed anymore
                var currentTargets = GetCurrentTargets();
                var targetsToRemove = _registeredHandlers.Keys
                    .Where(t => !currentTargets.Contains(t))
                    .ToList();

                foreach (var target in targetsToRemove)
                {
                    if (_registeredHandlers.TryGetValue(target, out var handler))
                    {
                        try
                        {
                            // The handler knows which session it belongs to
                            _registeredHandlers.Remove(target);
                            Debug.WriteLine($"[Cleanup] Unregistered event handler for {target}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ERROR] Failed to unregister handler for {target}: {ex.Message}");
                        }
                    }
                }

                // Create a dictionary for quick process name lookup
                var processDict = new Dictionary<int, string>();

                // Build map of sessions by process name for quicker lookup
                var sessionsByProcess = new Dictionary<string, AudioSessionControl>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        int pid = (int)s.GetProcessID;

                        // Get the process name
                        string procName;
                        if (!processDict.TryGetValue(pid, out procName))
                        {
                            try
                            {
                                var proc = Process.GetProcessById(pid);
                                procName = proc.ProcessName.ToLowerInvariant();
                                processDict[pid] = procName;
                            }
                            catch
                            {
                                procName = "";
                            }
                        }

                        // Only store if we got a valid process name
                        if (!string.IsNullOrEmpty(procName))
                        {
                            sessionsByProcess[procName] = s;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] Mapping session in SyncMuteStates: {ex.Message}");
                    }
                }

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
                        continue;
                    }

                    // Check if we have a matching session in our dictionary
                    if (sessionsByProcess.TryGetValue(target, out var matchedSession))
                    {
                        isMuted = matchedSession.SimpleAudioVolume.Mute;

                        // Only register if not already registered for this target
                        if (!_registeredHandlers.ContainsKey(target))
                        {
                            try
                            {
                                var handler = new AudioSessionEventsHandler(ctrl);
                                matchedSession.RegisterEventClient(handler);
                                _registeredHandlers[target] = handler;
                                Debug.WriteLine($"[Event] Registered handler for {target}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ERROR] Failed to register handler for {target}: {ex.Message}");
                            }
                        }

                        ctrl.SetMuted(isMuted);
                        _audioService.ApplyVolumeToTarget(target, ctrl.CurrentVolume, isMuted);
                    }
                    else
                    {
                        // If not found in the dictionary, try the old approach
                        var matchedSessionOld = Enumerable.Range(0, sessions.Count)
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

                        if (matchedSessionOld != null)
                        {
                            isMuted = matchedSessionOld.SimpleAudioVolume.Mute;

                            // Only register if not already registered for this target
                            if (!_registeredHandlers.ContainsKey(target))
                            {
                                try
                                {
                                    var handler = new AudioSessionEventsHandler(ctrl);
                                    matchedSessionOld.RegisterEventClient(handler);
                                    _registeredHandlers[target] = handler;
                                    Debug.WriteLine($"[Event] Registered handler for {target}");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[ERROR] Failed to register handler for {target}: {ex.Message}");
                                }
                            }

                            ctrl.SetMuted(isMuted);
                            _audioService.ApplyVolumeToTarget(target, ctrl.CurrentVolume, isMuted);
                        }
                    }
                }

                _hasSyncedMuteStates = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] In SyncMuteStates: {ex.Message}");
            }
        }
        private void HandleSliderData(string data)
        {
            if (_isInitializing || string.IsNullOrWhiteSpace(data)) return;

            try
            {
                // Parse the data outside of the Dispatcher call to avoid potential issues
                string[] parts = data.Split('|');

                if (parts.Length == 0)
                {
                    return; // Skip empty data
                }

                // Use BeginInvoke instead of Invoke to avoid blocking the calling thread
                Dispatcher.BeginInvoke(new Action(() => {
                    try
                    {
                        if (_channelControls.Count != parts.Length)
                        {
                            GenerateSliders(parts.Length);
                            return; // Skip processing in this case - we just regenerated the sliders
                        }

                        for (int i = 0; i < parts.Length && i < _channelControls.Count; i++)
                        {
                            if (!float.TryParse(parts[i].Trim(), out float level)) continue;

                            level = Math.Clamp(level / 1023f, 0f, 1f);
                            if (InvertSliderCheckBox.IsChecked ?? false)
                                level = 1f - level;

                            var ctrl = _channelControls[i];
                            var targets = ctrl.AudioTargets;

                            if (targets.Count == 0) continue;

                            float currentVolume = ctrl.CurrentVolume;
                            if (Math.Abs(currentVolume - level) < 0.01f) continue;

                            ctrl.SmoothAndSetVolume(level, suppressEvent: _isInitializing, disableSmoothing: _disableSmoothing);

                            // Don't apply audio if we're still initializing
                            if (_isInitializing) continue;

                            // Apply volume to all targets for this control
                            ApplyVolumeToTargets(ctrl, targets, level);
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
        private void ApplyVolumeToTargets(ChannelControl ctrl, List<AudioTarget> targets, float level)
        {
            foreach (var target in targets)
            {
                try
                {
                    if (target.IsInputDevice)
                    {
                        // Apply to input device
                        ApplyInputDeviceVolume(target.Name, level, ctrl.IsMuted);
                    }
                    else
                    {
                        // Apply to output app
                        _audioService.ApplyVolumeToTarget(target.Name, level, ctrl.IsMuted);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Applying volume to {target.Name}: {ex.Message}");
                }
            }
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
        // Break out the input volume handling logic to a separate method to simplify the main method
        private void ApplyInputVolume(ChannelControl ctrl, string target, float level)
        {
            try
            {
                if (!_inputDeviceMap.TryGetValue(target, out var mic))
                {
                    mic = new MMDeviceEnumerator()
                        .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                        .FirstOrDefault(d => d.FriendlyName.Equals(target, StringComparison.OrdinalIgnoreCase));

                    if (mic != null)
                    {
                        _inputDeviceMap[target] = mic;
                    }
                }

                if (mic != null)
                {
                    float previous = _lastInputVolume.TryGetValue(target, out var lastVol) ? lastVol : -1f;

                    if (Math.Abs(previous - level) > 0.01f)
                    {
                        mic.AudioEndpointVolume.Mute = ctrl.IsMuted || level <= 0.01f;
                        mic.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                        _lastInputVolume[target] = level;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Setting mic volume: {ex.Message}");
            }
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
        private void ApplyVolumeToAllTargets(ChannelControl control, float level)
        {
            foreach (var target in control.AudioTargets)
            {
                if (target.IsInputDevice)
                {
                    // Apply to input device
                    ApplyInputVolume(control, target.Name, level);
                }
                else
                {
                    // Apply to output app
                    _audioService.ApplyVolumeToTarget(target.Name, level, control.IsMuted);
                }
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
                    SliderTargets = _channelControls.Select(c => c.AudioTargets).ToList(),
                    IsDarkTheme = isDarkTheme,
                    IsSliderInverted = InvertSliderCheckBox.IsChecked ?? false,
                    VuMeters = ShowSlidersCheckBox.IsChecked ?? true,
                    StartOnBoot = StartOnBootCheckBox.IsChecked ?? false,
                    StartMinimized = StartMinimizedCheckBox.IsChecked ?? false,
                    // Important: Save the input mode status for each control
                    InputModes = _channelControls.Select(c => c.InputModeCheckBox.IsChecked ?? false).ToList(),
                    DisableSmoothing = DisableSmoothingCheckBox.IsChecked ?? false
                };

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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
            }
        }

        // In MainWindow.xaml.cs, modify SerialPort_DataReceived method:

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            try
            {
                string incoming = _serialPort.ReadExisting();

                // Check buffer size and truncate if it gets too large
                if (_serialBuffer.Length > 8192) // 8KB limit
                {
                    _serialBuffer.Clear();
                    Debug.WriteLine("[WARNING] Serial buffer exceeded limit and was cleared");
                }

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
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Serial read: {ex.Message}");
                _serialBuffer.Clear(); // Clear the buffer on unexpected errors
            }
        }
        private void CleanupDeadSessions()
        {
            var processIds = new HashSet<int>();

            // Get current processes
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        processIds.Add(proc.Id);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] Failed to enumerate processes: {ex.Message}");
                return;
            }

            // Clean process name cache
            var deadPids = _processNameCache.Keys.Where(pid => !processIds.Contains(pid)).ToList();
            foreach (var pid in deadPids)
            {
                _processNameCache.Remove(pid);
            }

            // Trim session cache and properly release COM objects for removed sessions
            if (_sessionIdCache.Count > 100)
            {
                var sessionsToRemove = _sessionIdCache.Take(_sessionIdCache.Count - 100).ToList();

                foreach (var sessionTuple in sessionsToRemove)
                {
                    try
                    {
                        // Try to properly release the COM object
                        if (sessionTuple.session != null)
                        {
                            Marshal.ReleaseComObject(sessionTuple.session);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Error] Failed to release COM object: {ex.Message}");
                    }
                }

                _sessionIdCache = _sessionIdCache.Skip(_sessionIdCache.Count - 100).ToList();
            }

            // Limit size of input device map 
            if (_inputDeviceMap.Count > 20)
            {
                var keysToRemove = _inputDeviceMap.Keys.Take(_inputDeviceMap.Count - 10).ToList();
                foreach (var key in keysToRemove)
                {
                    _inputDeviceMap.Remove(key);
                }
            }

            Debug.WriteLine($"[Cleanup] Removed {deadPids.Count} dead processes from cache. " +
                           $"Process cache: {_processNameCache.Count}, " +
                           $"Session cache: {_sessionIdCache.Count}, " +
                           $"Input device cache: {_inputDeviceMap.Count}");
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

        // Update the UpdateMeters method in MainWindow.xaml.cs

        private void UpdateMeters(object? sender, EventArgs e)
        {
            if (!_metersEnabled || _isClosing) return;

            const float visualGain = 1.5f;
            const float systemCalibrationFactor = 2.0f;

            // Refresh output device reference every 5 seconds
            if ((DateTime.Now - _lastDeviceRefresh).TotalSeconds > 5)
            {
                try
                {
                    _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _lastDeviceRefresh = DateTime.Now;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Failed to refresh audio device: {ex.Message}");
                    return; // Skip this update if we can't get the device
                }
            }

            try
            {
                var sessions = _audioDevice.AudioSessionManager.Sessions;

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

                        // Track the highest peak level across all targets
                        float highestPeak = 0;
                        bool allMuted = true;

                        // Process input device targets
                        var inputTargets = targets.Where(t => t.IsInputDevice).ToList();
                        if (inputTargets.Any())
                        {
                            foreach (var target in inputTargets)
                            {
                                if (!_inputDeviceMap.TryGetValue(target.Name.ToLowerInvariant(), out var mic))
                                {
                                    mic = new MMDeviceEnumerator()
                                        .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                                        .FirstOrDefault(d => d.FriendlyName.Equals(target.Name, StringComparison.OrdinalIgnoreCase));

                                    if (mic != null)
                                        _inputDeviceMap[target.Name.ToLowerInvariant()] = mic;
                                }

                                if (mic != null)
                                {
                                    try
                                    {
                                        float peak = mic.AudioMeterInformation.MasterPeakValue;
                                        // No mute factor here - we'll apply it once at the end
                                        if (peak > highestPeak)
                                            highestPeak = peak;

                                        // Check if any device is not muted
                                        if (!mic.AudioEndpointVolume.Mute)
                                            allMuted = false;
                                    }
                                    catch
                                    {
                                        // Continue if we can't get peak for this device
                                    }
                                }
                            }
                        }

                        // Process output app targets
                        var outputTargets = targets.Where(t => !t.IsInputDevice).ToList();
                        if (outputTargets.Any())
                        {
                            // Special handling for system
                            var systemTarget = outputTargets.FirstOrDefault(t =>
                                string.Equals(t.Name, "system", StringComparison.OrdinalIgnoreCase));

                            if (systemTarget != null)
                            {
                                float peak = _audioDevice.AudioMeterInformation.MasterPeakValue;
                                float systemVol = _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                                peak *= systemVol * systemCalibrationFactor;

                                if (peak > highestPeak)
                                    highestPeak = peak;

                                if (!_audioDevice.AudioEndpointVolume.Mute)
                                    allMuted = false;
                            }

                            // Handle other output applications
                            foreach (var target in outputTargets.Where(t =>
                                !string.Equals(t.Name, "system", StringComparison.OrdinalIgnoreCase)))
                            {
                                // Find matching session for this target
                                AudioSessionControl? matchingSession = null;
                                string targetName = target.Name.ToLowerInvariant();

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

                                        if (sidFile == targetName || iidFile == targetName || procName == targetName ||
                                             sid.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
                                             iid.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
                                             targetName != null && targetName.Length > 2 &&
                                             sid.Contains(targetName, StringComparison.OrdinalIgnoreCase))
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
                                        if (peak > highestPeak)
                                            highestPeak = peak;

                                        if (!matchingSession.SimpleAudioVolume.Mute)
                                            allMuted = false;
                                    }
                                    catch
                                    {
                                        // Continue if we can't get peak for this session
                                    }
                                }
                            }
                        }

                        // Apply the highest peak with gain factor to the meter
                        float finalLevel = ctrl.IsMuted || allMuted ? 0 : Math.Min(highestPeak * visualGain, 1.0f);
                        ctrl.UpdateAudioMeter(finalLevel);
                    }
                    catch (Exception ex)
                    {
                        // Log the error but continue with other controls
                        Debug.WriteLine($"[ERROR] Updating meter: {ex.Message}");
                        ctrl.UpdateAudioMeter(0); // Reset meter on error
                    }
                }

                // Use lower priority to avoid UI lag
                Dispatcher.InvokeAsync(() => SliderPanel.InvalidateVisual(), DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Meter update: {ex.Message}");
            }
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

        // You'll also need to make _channelControls accessible to allow the ChannelControl to get its index
        // In MainWindow.xaml.cs, change the private field to:
        public List<ChannelControl> _channelControls = new();

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
            public List<List<AudioTarget>> SliderTargets { get; set; } = new();
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
