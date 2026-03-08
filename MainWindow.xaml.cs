using DeejNG.Classes;
using DeejNG.Core.Configuration;
using DeejNG.Core.Interfaces;
using DeejNG.Dialogs;
using DeejNG.Infrastructure.System;
using DeejNG.Models;
using DeejNG.Services;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        #region Public Fields

        public List<ChannelControl> _channelControls = new();

        #endregion

        #region Private Fields

        private const float INLINE_MUTE_TRIGGER = 9999f;

        private readonly Queue<List<AudioTarget>> _audioTargetListPool = new();
        private readonly HashSet<string> _cachedMappedApplications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly MMDeviceEnumerator _deviceEnumerator = new();

        private readonly DeviceCacheManager _deviceManager;
        private readonly HashSet<int> _inlineMutedChannels = new HashSet<int>();
        private readonly IOverlayService _overlayService;
        private readonly IPowerManagementService _powerManagementService;
        private readonly ProfileManager _profileManager;
        private readonly HidConnectionManager _hidManager;
        private readonly AppSettingsManager _settingsManager;
        private readonly ISystemIntegrationService _systemIntegrationService;
        private readonly HashSet<string> _tempStringSet = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<AudioTarget> _tempTargetList = new();
        private readonly TimerCoordinator _timerCoordinator;
        private readonly object _unmappedLock = new object();
        private readonly TimeSpan UNMAPPED_THROTTLE_INTERVAL = TimeSpan.FromMilliseconds(100);

        private bool _allowVolumeApplication = false;

        private MMDevice _audioDevice;
        private AudioService _audioService;
        private ButtonActionHandler _buttonActionHandler;
        private DefaultAudioDeviceSwitcher _deviceSwitcher;
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
        private DateTime _lastDeviceCacheTime = DateTime.MinValue;
        private DateTime _lastForcedCleanup = DateTime.MinValue;
        private DateTime _lastMeterSessionRefresh = DateTime.MinValue;
        private DateTime _lastUnmappedMeterUpdate = DateTime.MinValue;
        private bool _metersEnabled = true;
        private bool _playPauseState = false;
        private Dictionary<string, IAudioSessionEventsHandler> _registeredHandlers = new();
        private int _sessionCacheHitCount = 0;
        private List<(AudioSessionControl session, string sessionId, string instanceId)> _sessionIdCache = new();
        private AudioEndpointVolume _systemVolume;
        private bool _useExponentialVolume = false;
        private bool isDarkTheme = false;

        #endregion

        #region Public Constructors

        public MainWindow()
        {
            _isInitializing = true;

            RenderOptions.ProcessRenderMode = RenderMode.Default;

            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");

            _deviceManager = new DeviceCacheManager();
            _settingsManager = new AppSettingsManager();
            _profileManager = new ProfileManager(_settingsManager);
            _hidManager = new HidConnectionManager();
            _timerCoordinator = new TimerCoordinator();
            _overlayService = ServiceLocator.Get<IOverlayService>();
            _systemIntegrationService = ServiceLocator.Get<ISystemIntegrationService>();
            _powerManagementService = ServiceLocator.Get<IPowerManagementService>();

            _powerManagementService.SystemSuspending += OnSystemSuspending;
            _powerManagementService.SystemResuming += OnSystemResuming;
            _powerManagementService.SystemResumed += OnSystemResumed;

            _audioService = new AudioService();

            _profileManager.LoadProfiles();

            LoadSettingsWithoutSerialConnection();
            LoadProfilesIntoUI();

            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _systemVolume = _audioDevice.AudioEndpointVolume;
            _systemVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;

            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _timerCoordinator.InitializeTimers();
            _timerCoordinator.MeterUpdate += UpdateMeters;
            _timerCoordinator.SessionCacheUpdate += SessionCacheTimer_Tick;
            _timerCoordinator.ForceCleanup += ForceCleanupTimer_Tick;
            _timerCoordinator.PositionSave += PositionSaveTimer_Tick;

            _hidManager.DataReceived += HandleMixerData;

            _hidManager.Connected += () =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateConnectionStatus();
                }, DispatcherPriority.Background);
            };

            _hidManager.Disconnected += () =>
            {
                _allowVolumeApplication = false;

                Dispatcher.BeginInvoke(() =>
                {
                    UpdateConnectionStatus();
                }, DispatcherPriority.Background);
            };

            StartSessionCacheUpdater();

            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            IconHandler.AddIconToRemovePrograms("DeejNG");
            _systemIntegrationService.SetDisplayIcon();

            _overlayService.Initialize();
            _overlayService.PositionChanged += OnOverlayPositionChanged;
            _overlayService.UpdateSettings(_settingsManager.AppSettings);

            _isInitializing = false;

            if (_settingsManager.AppSettings.StartMinimized)
            {
                WindowState = WindowState.Minimized;
                Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }

            _timerCoordinator.StartForceCleanup();
            SetupAutomaticHidConnection();
            // This makes my vid and pid 0 for some reason wessel
            InitializeThemeSelector();

            VersionText.Text = GetApplicationVersion();
        }

        #endregion

        #region Public Methods

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

                    return char.ToUpper(name[0]) + name.Substring(1).ToLower();
                }
            }

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

        public void HandleSessionDisconnected(string targetName)
        {
            try
            {
                if (_registeredHandlers.TryGetValue(targetName, out var handler))
                {
                    _registeredHandlers.Remove(targetName);
                }
            }
            catch
            {
            }
        }

        public void SaveOverlayPosition(double x, double y)
        {
            if (_settingsManager.AppSettings != null)
            {
                _settingsManager.AppSettings.OverlayX = x;
                _settingsManager.AppSettings.OverlayY = y;

                var screenInfo = DeejNG.Core.Helpers.ScreenPositionManager.GetScreenInfo(x, y);
                _settingsManager.AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                _settingsManager.AppSettings.OverlayScreenBounds = screenInfo.Bounds;

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

        public void UpdateOverlayPosition(double x, double y)
        {
            _overlayService.UpdatePosition(x, y);
        }

        public void UpdateOverlaySettings(AppSettings newSettings)
        {
            _overlayService.UpdateSettings(newSettings);

            _settingsManager.AppSettings.OverlayEnabled = newSettings.OverlayEnabled;
            _settingsManager.AppSettings.OverlayOpacity = newSettings.OverlayOpacity;
            _settingsManager.AppSettings.OverlayTimeoutSeconds = newSettings.OverlayTimeoutSeconds;
            _settingsManager.AppSettings.OverlayTextColor = newSettings.OverlayTextColor;
            _settingsManager.AppSettings.OverlayX = newSettings.OverlayX;
            _settingsManager.AppSettings.OverlayY = newSettings.OverlayY;
            _settingsManager.AppSettings.OverlayScreenDevice = newSettings.OverlayScreenDevice;
            _settingsManager.AppSettings.OverlayScreenBounds = newSettings.OverlayScreenBounds;
            _settingsManager.AppSettings.NumberOfButtons = newSettings.NumberOfButtons;
            _settingsManager.AppSettings.ButtonMappings = newSettings.ButtonMappings;
            _settingsManager.AppSettings.ExcludedFromUnmapped = newSettings.ExcludedFromUnmapped ?? new List<string>();
            _settingsManager.AppSettings.HidVendorId = newSettings.HidVendorId;
            _settingsManager.AppSettings.HidProductId = newSettings.HidProductId;
            _settingsManager.AppSettings.AudioDeviceOneFriendlyName = newSettings.AudioDeviceOneFriendlyName;
            _settingsManager.AppSettings.AudioDeviceOneId = newSettings.AudioDeviceOneId;
            _settingsManager.AppSettings.MicrophoneDeviceOneFriendlyName = newSettings.MicrophoneDeviceOneFriendlyName;
            _settingsManager.AppSettings.MicrophoneDeviceOneId = newSettings.MicrophoneDeviceOneId;
            _settingsManager.AppSettings.AudioDeviceTwoFriendlyName = newSettings.AudioDeviceTwoFriendlyName;
            _settingsManager.AppSettings.AudioDeviceTwoId = newSettings.AudioDeviceTwoId;
            _settingsManager.AppSettings.MicrophoneDeviceTwoFriendlyName = newSettings.MicrophoneDeviceTwoFriendlyName;
            _settingsManager.AppSettings.MicrophoneDeviceTwoId = newSettings.MicrophoneDeviceTwoId;
            _settingsManager.AppSettings.AudioDeviceThreeFriendlyName = newSettings.AudioDeviceThreeFriendlyName;
            _settingsManager.AppSettings.AudioDeviceThreeId = newSettings.AudioDeviceThreeId;
            _settingsManager.AppSettings.MicrophoneDeviceThreeFriendlyName = newSettings.MicrophoneDeviceThreeFriendlyName;
            _settingsManager.AppSettings.MicrophoneDeviceThreeId = newSettings.MicrophoneDeviceThreeId;

            ConfigureButtonLayout();
            SaveSettings();
        }

        public void ConnectWithNewVidAndPid(int vid, int pid)
        {
            _hidManager.ChangeVidPid(vid, pid);
        }

        #endregion

        #region Protected Methods

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;

            if (_settingsManager?.AppSettings != null && _profileManager != null)
            {
                try
                {
                    _profileManager.UpdateActiveProfileSettings(_settingsManager.AppSettings);
                    _profileManager.SaveProfiles();
                }
                catch
                {
                }
            }

            _timerCoordinator.StopAll();

            if (_powerManagementService != null)
            {
                _powerManagementService.SystemSuspending -= OnSystemSuspending;
                _powerManagementService.SystemResuming -= OnSystemResuming;
                _powerManagementService.SystemResumed -= OnSystemResumed;
            }

            _overlayService?.Dispose();
            _powerManagementService?.Dispose();

            foreach (var target in _registeredHandlers.Keys.ToList())
            {
                try
                {
                    _registeredHandlers.Remove(target);
                }
                catch
                {
                }
            }

            _hidManager?.Dispose();
            _deviceManager?.Dispose();
            _timerCoordinator?.Dispose();

            try
            {
                if (_systemVolume != null)
                {
                    _systemVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                }
            }
            catch
            {
            }

            foreach (var sessionTuple in _sessionIdCache)
            {
                try
                {
                    if (sessionTuple.session != null)
                    {
                        Marshal.FinalReleaseComObject(sessionTuple.session);
                    }
                }
                catch
                {
                }
            }

            _sessionIdCache.Clear();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            base.OnClosed(e);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }
            else
            {
                MyNotifyIcon.Visibility = Visibility.Collapsed;
            }

            base.OnStateChanged(e);
        }

        #endregion

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

                var existingTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == themeUri);
                if (existingTheme == null)
                {
                    existingTheme = new ResourceDictionary() { Source = themeUri };
                    Application.Current.Resources.MergedDictionaries.Add(existingTheme);
                }

                var otherThemeUri = isDarkTheme
                    ? new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml")
                    : new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");

                var currentTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == otherThemeUri);
                if (currentTheme != null)
                {
                    Application.Current.Resources.MergedDictionaries.Remove(currentTheme);
                }
            }
            catch
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

                            var excludedApps = _settingsManager.AppSettings?.ExcludedFromUnmapped;
                            _audioService.ApplyVolumeToUnmappedApplications(level, ctrl.IsMuted, mappedApps, excludedApps);
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
                catch
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

                    SliderPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Size desiredChannels = SliderPanel.DesiredSize;

                    double width = Math.Min(Math.Max(desiredChannels.Width + 60, 900), work.Width - 40);
                    if (Math.Abs(Width - width) > 2)
                        Width = width;

                    double titleH = TitleBarGrid?.ActualHeight > 0 ? TitleBarGrid.ActualHeight : 48;
                    double toolbarH = 44;
                    double statusH = StatusBar?.ActualHeight ?? 0;
                    double contentH = Math.Max(desiredChannels.Height + 40, 320);
                    double desired = titleH + toolbarH + contentH + statusH + 60;
                    double height = Math.Min(desired, work.Height - 20);

                    if (Math.Abs(Height - height) > 2)
                        Height = height;
                }, DispatcherPriority.Render);
            }
            catch
            {
            }
        }

        private void ButtonIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is Border border && border.DataContext is ButtonIndicatorViewModel viewModel)
                {
                    var settings = _settingsManager.AppSettings;
                    if (settings == null || settings.ButtonMappings == null) return;

                    var mapping = settings.ButtonMappings.FirstOrDefault(m => m.ButtonIndex == viewModel.ButtonIndex);
                    if (mapping == null || mapping.Action == ButtonAction.None) return;

                    if (_settingsManager == null)
                    {
                        throw new InvalidOperationException("Settings manager is not initialized.");
                    }

                    if (_deviceSwitcher == null)
                    {
                        _deviceSwitcher = new DefaultAudioDeviceSwitcher();
                    }

                    if (_buttonActionHandler == null)
                    {
                        _buttonActionHandler = new ButtonActionHandler(_channelControls, _deviceSwitcher, _settingsManager);
                    }

                    bool isMuteAction = mapping.Action == ButtonAction.MuteChannel ||
                                        mapping.Action == ButtonAction.GlobalMute;
                    bool isPlayPauseAction = mapping.Action == ButtonAction.MediaPlayPause;
                    bool isMomentaryAction = mapping.Action == ButtonAction.MediaNext ||
                                             mapping.Action == ButtonAction.MediaPrevious ||
                                             mapping.Action == ButtonAction.MediaStop;

                    if (isMomentaryAction)
                    {
                        viewModel.PressType = ButtonPressType.Short;

                        Task.Delay(150).ContinueWith(_ =>
                        {
                            Dispatcher.BeginInvoke(() =>
                            {
                                viewModel.PressType = ButtonPressType.None;
                            });
                        });
                    }

                    _buttonActionHandler.ExecuteAction(mapping);

                    if (isMuteAction)
                    {
                        ButtonPressType muteState = ButtonPressType.None;

                        if (mapping.Action == ButtonAction.MuteChannel &&
                            mapping.TargetChannelIndex >= 0 &&
                            mapping.TargetChannelIndex < _channelControls.Count)
                        {
                            muteState = _channelControls[mapping.TargetChannelIndex].IsMuted ? ButtonPressType.Short : ButtonPressType.None;
                        }
                        else if (mapping.Action == ButtonAction.GlobalMute)
                        {
                            muteState = _channelControls.Any(c => c.IsMuted) ? ButtonPressType.Short : ButtonPressType.None;
                        }

                        viewModel.PressType = muteState;
                    }
                    else if (isPlayPauseAction)
                    {
                        _playPauseState = !_playPauseState;
                        viewModel.PressType = _playPauseState ? ButtonPressType.Short : ButtonPressType.None;
                    }
                }
            }
            catch
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
            catch
            {
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void ComPortSelector_DropDownOpened(object sender, EventArgs e)
        {
            // Left intentionally empty in the HID-only version.
            // Keep this only if XAML still references the event.
        }

        private void ConfigureButtonLayout()
        {
            try
            {

                if (_settingsManager == null)
                {
                    throw new InvalidOperationException("Settings manager is not initialized.");
                }

                if (_deviceSwitcher == null)
                {
                    _deviceSwitcher = new DefaultAudioDeviceSwitcher();
                }

                if (_buttonActionHandler == null)
                {
                    _buttonActionHandler = new ButtonActionHandler(_channelControls, _deviceSwitcher, _settingsManager);
                }

                UpdateButtonIndicators();
            }
            catch
            {
            }
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_hidManager.IsConnected)
            {
                _hidManager.Stop();
            }
            else
            {
                ConnectButton.IsEnabled = false;
                ConnectButton.Content = "Connecting...";

                if (_profileManager == null)
                {
                    return;
                }

                var activeProfile = _profileManager.ActiveProfile;

                if (activeProfile == null)
                {
                    return;
                }

                _hidManager.Init(activeProfile.Settings.HidVendorId, activeProfile.Settings.HidProductId, true);

                var resetTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };

                resetTimer.Tick += (s, args) =>
                {
                    resetTimer.Stop();
                    UpdateConnectionStatus();
                };

                resetTimer.Start();
            }
        }

        private void CreateNotifyIconContextMenu()
        {
            try
            {
                ContextMenu contextMenu = new ContextMenu();

                MenuItem showHideMenuItem = new MenuItem();
                showHideMenuItem.Header = "Show/Hide";
                showHideMenuItem.Click += ShowHideMenuItem_Click;

                MenuItem exitMenuItem = new MenuItem();
                exitMenuItem.Header = "Exit";
                exitMenuItem.Click += ExitMenuItem_Click;

                contextMenu.Items.Add(showHideMenuItem);
                contextMenu.Items.Add(new Separator());
                contextMenu.Items.Add(exitMenuItem);

                MyNotifyIcon.ContextMenu = contextMenu;
            }
            catch
            {
            }
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string currentName = _profileManager.ActiveProfile.Name;

                if (_profileManager.ProfileCollection.Profiles.Count <= 1)
                {
                    ConfirmationDialog.ShowOK("Cannot Delete",
                        "Cannot delete the last profile. At least one profile must exist.", this);
                    return;
                }

                var result = ConfirmationDialog.ShowYesNo("Confirm Delete",
                    $"Are you sure you want to delete profile '{currentName}'?\n\nThis action cannot be undone.", this);

                if (result == ConfirmationDialog.ButtonResult.Yes)
                {
                    if (_profileManager.DeleteProfile(currentName))
                    {
                        LoadProfilesIntoUI();
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
            _isExiting = true;
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

                        string cleanedProcessName = Path.GetFileNameWithoutExtension(processName).ToLowerInvariant();

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
            catch
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

                if (_deviceManager.ShouldRefreshCache())
                {
                    _deviceManager.RefreshCaches();
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                _lastForcedCleanup = DateTime.Now;
            }
            catch
            {
            }
        }

        private void GenerateSliders(int count)
        {
            SliderPanel.Children.Clear();
            _channelControls.Clear();

            var savedSettings = _profileManager.GetActiveProfileSettings();
            var savedTargetGroups = savedSettings?.SliderTargets ?? new List<List<AudioTarget>>();

            Debug.WriteLine($"[GenerateSliders] Active profile: {_profileManager.ActiveProfile?.Name}");
            Debug.WriteLine($"[GenerateSliders] Slider count: {count}, Target groups count: {savedTargetGroups.Count}");

            for (int g = 0; g < savedTargetGroups.Count; g++)
            {
                var targets = savedTargetGroups[g];
                Debug.WriteLine($"[GenerateSliders] Group {g}: {string.Join(", ", targets.Select(t => t?.Name ?? "null"))}");
            }

            _isInitializing = true;
            _allowVolumeApplication = false;

            for (int i = 0; i < count; i++)
            {
                var control = new ChannelControl();
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
            ConfigureButtonLayout();
        }

        private string GetApplicationVersion()
        {
            try
            {
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
            catch
            {
            }

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
                        catch
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            catch
            {
            }

            return highestPeak;
        }

        private void HandleMixerData(HidMixerState data)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (data.NumSliders <= 0)
                            return;

                        // Hardware slider count takes priority
                        if (_channelControls.Count != data.NumSliders)
                        {
                            var currentTargets = _channelControls
                                .Select(c => c.AudioTargets)
                                .ToList();

                            _expectedSliderCount = data.NumSliders;
                            GenerateSliders(data.NumSliders);
                            AutoSizeToChannels();

                            for (int i = 0; i < Math.Min(currentTargets.Count, _channelControls.Count); i++)
                            {
                                _channelControls[i].AudioTargets = currentTargets[i];
                            }

                            SaveSettings();
                            return;
                        }

                        // Process sliders
                        int maxSliderIndex = Math.Min(data.NumSliders, _channelControls.Count);

                        for (int i = 0; i < maxSliderIndex; i++)
                        {
                            var ctrl = _channelControls[i];
                            var targets = ctrl.AudioTargets;

                            if (targets == null || targets.Count == 0)
                                continue;

                            byte packedSlider = data.Sliders[i];

                            // bit 7 = change flag
                            bool hasChanged = (packedSlider & 0b1000_0000) != 0;

                            // Optional: skip processing if nothing changed
                            if (!hasChanged)
                                continue;

                            // bits 0..6 = slider value (0..100)
                            byte sliderValue = (byte)(packedSlider & 0b0111_1111);

                            float level = sliderValue / 100f;
                            level = Math.Clamp(level, 0f, 1f);

                            if (InvertSliderCheckBox.IsChecked ?? false)
                            {
                                level = 1f - level;
                            }

                            // This doesn't work anymore now that we use HID and only get updates when we change a slider, but I'll leave it in case we add back any kind of polling-based input in the future.
                            if (_useExponentialVolume)
                            {
                                level = (MathF.Pow(_exponentialVolumeFactor, level) / (_exponentialVolumeFactor - 1f))
                                      - (1f / (_exponentialVolumeFactor - 1f));
                            }

                            ctrl.SmoothAndSetVolume(
                                level,
                                suppressEvent: !_allowVolumeApplication,
                                disableSmoothing: _disableSmoothing
                            );

                            if (_allowVolumeApplication)
                            {
                                ApplyVolumeToTargets(ctrl, targets, level);
                            }
                        }

                        // Buttons
                        // For now: only forward non-zero button bytes to your existing handler.
                        // Later you can decode short/long press values here however you want.
                        for (int i = 0; i < data.NumButtons; i++)
                        {
                            byte buttonValue = data.Buttons[i];
                            ButtonPressType pressType = (ButtonPressType)buttonValue;

                            if (pressType != ButtonPressType.None)
                            {
                                HandleButtonPress(i, pressType);
                            }
                        }

                        // Enable volume application after first successful packet
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

                        // Optional: show overlay only after processing
                        if (_allowVolumeApplication)
                        {
                            ShowVolumeOverlayIfSafe();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"HandleMixerData UI error: {ex}");
                    }
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HandleMixerData dispatch error: {ex}");
            }
        }

        private void HandleButtonPress(int buttonIndex, ButtonPressType pressType)
        {
            // TO-DO: Implement a button press timeout since button presses can lead to heavy function calls and we don't want multiple presses stacking up if the user holds a button down or if the device sends repeated presses.
            try
            {
                var settings = _settingsManager.AppSettings;
                if (settings == null || settings.ButtonMappings == null) return;

                // Find the mapping for this button
                var mapping = settings.ButtonMappings.FirstOrDefault(m => m.ButtonIndex == buttonIndex && m.PressType == pressType);
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

                if (_settingsManager == null)
                {
                    throw new InvalidOperationException("Settings manager is not initialized.");
                }

                if (_deviceSwitcher == null)
                {
                    _deviceSwitcher = new DefaultAudioDeviceSwitcher();
                }

                // Ensure button handler is initialized (do this outside dispatcher to avoid repeated checks)
                if (_buttonActionHandler == null)
                {
                    _buttonActionHandler = new ButtonActionHandler(_channelControls, _deviceSwitcher, _settingsManager);
                }

                // OPTIMIZATION: Single dispatcher call handles all UI updates and action execution
                // This reduces handle accumulation from repeated BeginInvoke calls
                Dispatcher.BeginInvoke(() =>
                {
                    var indicator = _buttonIndicators.FirstOrDefault(b => b.ButtonIndex == buttonIndex && b.PressType == pressType);

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
                            ButtonPressType muteState = ButtonPressType.None;

                            if (mapping.Action == ButtonAction.MuteChannel &&
                                mapping.TargetChannelIndex >= 0 &&
                                mapping.TargetChannelIndex < _channelControls.Count)
                            {
                                muteState = _channelControls[mapping.TargetChannelIndex].IsMuted ? ButtonPressType.Short : ButtonPressType.None;
                            }
                            else if (mapping.Action == ButtonAction.GlobalMute)
                            {
                                // Global mute - check if any channel is muted
                                muteState = _channelControls.Any(c => c.IsMuted) ? ButtonPressType.Short : ButtonPressType.None;
                            }

                            if (indicator.PressType != muteState)
                            {
                                indicator.PressType = muteState;

                            }
                        }
                        // For play/pause, toggle the state
                        else if (isPlayPauseAction)
                        {
                            _playPauseState = !_playPauseState;
                            indicator.PressType = _playPauseState ? ButtonPressType.Short : ButtonPressType.None;

                        }
                    }
                });
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

            var profile = _profileManager.GetActiveProfileSettings();
            var setting = _settingsManager.AppSettings;

            string savedTheme = _settingsManager.AppSettings.SelectedTheme ?? "Dark";
            var selectedTheme = themes.FirstOrDefault(t => t.Name == savedTheme) ?? themes[0];
            ThemeSelector.SelectedItem = selectedTheme; // After this vid and pid are 0
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

        private void LoadSettingsWithoutSerialConnection()
        {
            _isInitializing = true;

            try
            {
                var settings = _profileManager.GetActiveProfileSettings();
                _settingsManager.AppSettings = settings;

                if (!string.IsNullOrEmpty(settings.SelectedTheme))
                {
                }
                else
                {
                    ApplyTheme(settings.IsDarkTheme ? "Dark" : "Light");
                }

                InvertSliderCheckBox.IsChecked = settings.IsSliderInverted;
                ShowSlidersCheckBox.IsChecked = settings.VuMeters;
                SetMeterVisibilityForAll(settings.VuMeters);
                DisableSmoothingCheckBox.IsChecked = settings.DisableSmoothing;
                UseExponentialVolumeCheckBox.IsChecked = settings.UseExponentialVolume;
                ExponentialVolumeFactorSlider.Value = settings.ExponentialVolumeFactor;

                _settingsManager.ValidateOverlayPosition();

                StartOnBootCheckBox.Checked -= StartOnBootCheckBox_Checked;
                StartOnBootCheckBox.Unchecked -= StartOnBootCheckBox_Unchecked;

                bool isInStartup = _systemIntegrationService.IsStartupEnabled();
                settings.StartOnBoot = isInStartup;
                StartOnBootCheckBox.IsChecked = isInStartup;

                StartOnBootCheckBox.Checked += StartOnBootCheckBox_Checked;
                StartOnBootCheckBox.Unchecked += StartOnBootCheckBox_Unchecked;

                StartMinimizedCheckBox.IsChecked = settings.StartMinimized;
                StartMinimizedCheckBox.Checked += StartMinimizedCheckBox_Checked;

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

                _overlayService.UpdateSettings(settings);

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

                _isInitializing = false;
            }
            catch
            {
                _expectedSliderCount = 4;
                GenerateSliders(4);
                _hasLoadedInitialSettings = true;
                _isInitializing = false;
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
                return;
            }

            _isClosing = true;

            try
            {
                _timerCoordinator?.Dispose();
                _hidManager?.Dispose();
                _overlayService?.Dispose();
                _deviceEnumerator?.Dispose();
                ServiceLocator.Dispose();
            }
            catch
            {
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SliderPanel.Visibility = Visibility.Visible;
            StartOnBootCheckBox.IsChecked = _settingsManager.AppSettings.StartOnBoot;

            Dispatcher.BeginInvoke(() => AutoSizeToChannels(), DispatcherPriority.ApplicationIdle);
        }

        private void MaxButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void MinButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MyNotifyIcon_Click(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Show();
                WindowState = WindowState.Normal;
                InvalidateMeasure();
                UpdateLayout();
            }
            else
            {
                WindowState = WindowState.Minimized;
                Hide();
            }
        }

        private void NewProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettings();

                string newName = InputDialog.Show("New Profile", "Enter a name for the new profile:", "", this);

                if (string.IsNullOrWhiteSpace(newName))
                    return;

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

                var screenInfo = DeejNG.Core.Helpers.ScreenPositionManager.GetScreenInfo(e.X, e.Y);
                _settingsManager.AppSettings.OverlayScreenDevice = screenInfo.DeviceName;
                _settingsManager.AppSettings.OverlayScreenBounds = screenInfo.Bounds;

                _timerCoordinator.TriggerPositionSave();
            }
        }

        private void OnSystemResumed(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    RefreshAudioDevices();
                    _timerCoordinator.StartMeters();
                    _timerCoordinator.StartSessionCache();

                    UpdateSessionCache();

                    _hasSyncedMuteStates = false;
                    SyncMuteStates();

                    var enableTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(500)
                    };

                    enableTimer.Tick += (s, args) =>
                    {
                        enableTimer.Stop();
                        _allowVolumeApplication = true;
                    };

                    enableTimer.Start();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OnSystemResumed failed: {ex}");
                }
            }, DispatcherPriority.Background);
        }

        private void OnSystemResuming(object sender, EventArgs e)
        {
            _allowVolumeApplication = false;
        }

        private void OnSystemSuspending(object sender, EventArgs e)
        {
            _timerCoordinator.StopMeters();
            _timerCoordinator.StopSessionCache();

            if (_settingsManager?.AppSettings != null)
            {
                try
                {
                    _settingsManager.SaveSettings(_settingsManager.AppSettings);
                }
                catch
                {
                }
            }

            _overlayService?.HideOverlay();
        }

        private void PositionSaveTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                _profileManager.UpdateActiveProfileSettings(_settingsManager.AppSettings);
                _profileManager.SaveProfiles();
            }
            catch
            {
            }
        }

        private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || ProfileSelector.SelectedItem == null)
                return;

            string selectedProfile = ProfileSelector.SelectedItem.ToString();
            string currentProfile = _profileManager.ActiveProfile?.Name ?? "unknown";

            Debug.WriteLine($"[Profile] Switching from '{currentProfile}' to '{selectedProfile}'");

            var currentTargets = _channelControls.Select(c => c.AudioTargets?.FirstOrDefault()?.Name ?? "empty").ToList();
            Debug.WriteLine($"[Profile] Current UI targets before save: {string.Join(", ", currentTargets)}");

            SaveSettings();

            var savedSettings = _profileManager.GetActiveProfileSettings();
            var savedTargets = savedSettings?.SliderTargets?.Select(t => t?.FirstOrDefault()?.Name ?? "empty").ToList() ?? new List<string>();
            Debug.WriteLine($"[Profile] Saved targets for '{currentProfile}': {string.Join(", ", savedTargets)}");

            if (_profileManager.SwitchToProfile(selectedProfile))
            {
                var newSettings = _profileManager.GetActiveProfileSettings();
                var newTargets = newSettings?.SliderTargets?.Select(t => t?.FirstOrDefault()?.Name ?? "empty").ToList() ?? new List<string>();
                Debug.WriteLine($"[Profile] New profile '{selectedProfile}' targets: {string.Join(", ", newTargets)}");

                LoadSettingsWithoutSerialConnection();

                var loadedTargets = _channelControls.Select(c => c.AudioTargets?.FirstOrDefault()?.Name ?? "empty").ToList();
                Debug.WriteLine($"[Profile] UI targets after load: {string.Join(", ", loadedTargets)}");

                ConfirmationDialog.ShowOK("Profile Changed",
                    $"Switched to profile: {selectedProfile}", this);
            }
        }

        private void RefreshAudioDevices()
        {
            try
            {
                _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _systemVolume = _audioDevice.AudioEndpointVolume;

                _systemVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                _systemVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;

                _deviceManager.RefreshCaches();

                _sessionIdCache.Clear();
                _cachedSessionsForMeters = null;
                _cachedAudioDevice = null;
            }
            catch
            {
            }
        }

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
                Debug.WriteLine($"[SaveSettings] Skipped - _isInitializing={_isInitializing}, _hasLoadedInitialSettings={_hasLoadedInitialSettings}");
                return;
            }

            try
            {
                if (_channelControls.Count == 0)
                {
                    Debug.WriteLine("[SaveSettings] Skipped - no channel controls");
                    return;
                }

                var sliderTargets = _channelControls.Select(c => c.AudioTargets ?? new List<AudioTarget>()).ToList();
                Debug.WriteLine($"[SaveSettings] Saving to profile '{_profileManager.ActiveProfile?.Name}'");

                for (int i = 0; i < sliderTargets.Count; i++)
                {
                    var targets = sliderTargets[i];
                    Debug.WriteLine($"[SaveSettings]   Slider {i}: {string.Join(", ", targets.Select(t => t?.Name ?? "null"))} (Count={targets.Count})");
                }

                var settings = _settingsManager.CreateSettingsFromUI(
                    "hid",
                    sliderTargets,
                    isDarkTheme,
                    InvertSliderCheckBox.IsChecked ?? false,
                    ShowSlidersCheckBox.IsChecked ?? true,
                    StartOnBootCheckBox.IsChecked ?? false,
                    StartMinimizedCheckBox.IsChecked ?? false,
                    DisableSmoothingCheckBox.IsChecked ?? false,
                    UseExponentialVolumeCheckBox.IsChecked ?? false,
                    (float)ExponentialVolumeFactorSlider.Value,
                    9600
                );

                _profileManager.UpdateActiveProfileSettings(settings);
                _profileManager.SaveProfiles();
                _settingsManager.SaveSettings(settings);
            }
            catch
            {
            }
        }

        private void SessionCacheTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing) return;

            try
            {
                UpdateSessionCache();
            }
            catch
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

        private void SetupAutomaticHidConnection()
        {
            var startupTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            startupTimer.Tick += (s, e) =>
            {
                startupTimer.Stop();

                try
                {
                    if (!_hidManager.IsConnected)
                    {
                        if (_profileManager == null)
                        {
                            return;
                        }

                        var activeProfile = _profileManager.ActiveProfile;

                        if (activeProfile == null)
                        {
                            return;
                        }

                        _hidManager.Init(activeProfile.Settings.HidVendorId, activeProfile.Settings.HidProductId, true);
                    }

                    UpdateConnectionStatus();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HID] Auto-connect failed: {ex}");
                    UpdateConnectionStatus();
                }
            };

            startupTimer.Start();
        }

        private void ShowHideMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (IsVisible)
            {
                Hide();
            }
            else
            {
                Show();
                WindowState = WindowState.Normal;
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

        private void ShowVolumeOverlayIfSafe()
        {
            if (IsActive ||
                _settingsManager.AppSettings.OverlayTimeoutSeconds == AppSettings.OverlayNoTimeout ||
                (IsVisible && WindowState != WindowState.Minimized))
            {
                ShowVolumeOverlay();
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

                var handlersToRemove = new List<string>(_registeredHandlers.Keys);
                foreach (var target in handlersToRemove)
                {
                    try
                    {
                        _registeredHandlers.Remove(target);
                    }
                    catch
                    {
                    }
                }

                _registeredHandlers.Clear();

                var allMappedApps = GetAllMappedApplications();
                var processDict = new Dictionary<int, string>();
                var sessionsByProcess = new Dictionary<string, AudioSessionControl>(StringComparer.OrdinalIgnoreCase);
                var sessionProcessIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
                    catch
                    {
                    }
                }

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
                        catch
                        {
                        }
                    }
                }

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
                            var excludedApps = _settingsManager.AppSettings?.ExcludedFromUnmapped;
                            _audioService.ApplyMuteStateToUnmappedApplications(ctrl.IsMuted, mappedApps, excludedApps);
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
            catch
            {
            }
        }

        private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || ThemeSelector.SelectedItem is not ThemeOption selectedTheme)
                return;

            try
            {
                var themesToRemove = Application.Current.Resources.MergedDictionaries
                    .Where(d => d.Source != null && d.Source.OriginalString.Contains("/Themes/") && d.Source.OriginalString.EndsWith("Theme.xaml"))
                    .ToList();

                foreach (var theme in themesToRemove)
                {
                    Application.Current.Resources.MergedDictionaries.Remove(theme);
                }

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

                _settingsManager.AppSettings.SelectedTheme = selectedTheme.Name;
                SaveSettings();
            }
            catch
            {
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
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

            Dispatcher.Invoke(() =>
            {
                foreach (var control in _channelControls)
                {
                    control.TargetTextBox.ClearValue(TextBox.ForegroundProperty);
                    control.InvalidateVisual();
                }

                InvalidateVisual();
                UpdateLayout();
            }, DispatcherPriority.Render);

            SaveSettings();
        }

        private void UpdateButtonIndicators()
        {
            if (Dispatcher.CheckAccess())
            {
                UpdateButtonIndicatorsCore();
            }
            else
            {
                Dispatcher.BeginInvoke(UpdateButtonIndicatorsCore);
            }
        }

        private void UpdateButtonIndicatorsCore()
        {
            try
            {
                var settings = _settingsManager.AppSettings;

                _buttonIndicators.Clear();

                if (settings?.ButtonMappings != null && settings.ButtonMappings.Count > 0)
                {
                    foreach (var mapping in settings.ButtonMappings.OrderBy(m => m.ButtonIndex))
                    {
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
                            PressType = ButtonPressType.None
                        };

                        _buttonIndicators.Add(indicator);
                    }

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
            catch
            {
            }
        }

        private void UpdateConnectionStatus()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => UpdateConnectionStatus(), DispatcherPriority.Background);
                return;
            }

            if (_hidManager.IsConnected)
            {
                ConnectionStatus.Text = "Connected";
                ConnectionStatus.Foreground = Brushes.Green;
                ConnectButton.Content = "Disconnect";
            }
            else
            {
                ConnectionStatus.Text = "Waiting for HID device...";
                ConnectionStatus.Foreground = Brushes.Orange;
                ConnectButton.Content = "Connect";
            }

            ConnectButton.IsEnabled = true;
        }

        private void UpdateMeters(object? sender, EventArgs e)
        {
            if (!_metersEnabled || _isClosing) return;

            const float visualGain = 1.5f;
            const float systemCalibrationFactor = 2.0f;

            try
            {
                if (_cachedAudioDevice == null ||
                    (DateTime.Now - _lastDeviceCacheTime) > TimeSpan.FromSeconds(2))
                {
                    _cachedAudioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _lastDeviceCacheTime = DateTime.Now;
                }

                if (_cachedAudioDevice == null)
                    return;

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
                                        ctrl.UpdateAudioMeter(peak > 0.01f ? peak * visualGain : 0);
                                    }
                                }
                                else if (target.IsOutputDevice)
                                {
                                    if (_deviceManager.TryGetOutputPeak(target.Name, out var peak, out var muted))
                                    {
                                        ctrl.UpdateAudioMeter(peak > 0.01f ? peak * visualGain : 0);
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
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                        ctrl.UpdateAudioMeter(0);
                    }
                }
            }
            catch
            {
            }
        }

        private void UpdateMuteButtonIndicators()
        {
            if (Dispatcher.CheckAccess())
            {
                UpdateMuteButtonIndicatorsCore();
            }
            else
            {
                Dispatcher.BeginInvoke(UpdateMuteButtonIndicatorsCore);
            }
        }

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

                    var indicator = _buttonIndicators.FirstOrDefault(b => b.ButtonIndex == mapping.ButtonIndex && (b.PressType == ButtonPressType.Short || b.PressType == ButtonPressType.Long));
                    if (indicator == null) continue;

                    ButtonPressType muteState = ButtonPressType.None;

                    if (mapping.Action == ButtonAction.MuteChannel &&
                        mapping.TargetChannelIndex >= 0 &&
                        mapping.TargetChannelIndex < _channelControls.Count)
                    {
                        muteState = _channelControls[mapping.TargetChannelIndex].IsMuted ? ButtonPressType.Short : ButtonPressType.None;
                    }
                    else if (mapping.Action == ButtonAction.GlobalMute)
                    {
                        muteState = _channelControls.Any(c => c.IsMuted) ? ButtonPressType.Short : ButtonPressType.None;
                    }

                    if (indicator.PressType != muteState)
                    {
                        indicator.PressType = muteState;
                    }
                }
            }
            catch
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
                catch
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
            catch
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

        #endregion

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
        }

        #endregion
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