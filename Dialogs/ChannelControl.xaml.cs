using DeejNG.Models;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeejNG.Dialogs
{
    public partial class ChannelControl : UserControl
    {

        #region Private Fields

        private const float ClipThreshold = 0.98f;
        private const float SmoothingFactor = 0.1f;
        private readonly Brush _muteOffBrush = Brushes.Gray;
        private readonly Brush _muteOnBrush = new SolidColorBrush(Color.FromRgb(255, 64, 64));
        private readonly TimeSpan PeakHoldDuration = TimeSpan.FromSeconds(1);
        private bool _isMuted = false;
        private List<AudioTarget> _audioTargets = new();
        private bool _layoutReady = false;
        private float _meterLevel;
        private float _peakLevel;
        private DateTime _peakTimestamp;
        private float _smoothedVolume;
        private bool _suppressEvents = false;

        #endregion Private Fields

        #region Public Constructors

        // Bright red
        public ChannelControl()
        {
            InitializeComponent();
            Loaded += ChannelControl_Loaded;
            MouseDoubleClick += ChannelControl_MouseDoubleClick;
        }

        #endregion Public Constructors

        #region Public Events

        public event EventHandler TargetChanged;
        public event Action<List<AudioTarget>, float, bool> VolumeOrMuteChanged;
   

        #endregion Public Events

        #region Public Properties

        public float CurrentVolume => (float)VolumeSlider.Value;
        public bool IsInputMode
        {
            get => _audioTargets.Any(t => t.IsInputDevice);
            set
            {
                InputModeCheckBox.IsChecked = value;
                // If checked and we don't have any input devices,
                // we should present the picker dialog
            }
        }
        public bool IsMuted => _isMuted;
        public string TargetExecutable =>
             _audioTargets.FirstOrDefault()?.Name ?? "";
        public List<AudioTarget> AudioTargets
        {
            get => _audioTargets;
            set
            {
                _audioTargets = value ?? new List<AudioTarget>();
                UpdateTargetsDisplay();
            }
        }
        #endregion Public Properties

        #region Public Methods

        public void SetMeterVisibility(bool visible)
        {
            MeterVisuals.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetMuted(bool muted)
        {
            _suppressEvents = true;
            _isMuted = muted;
            MuteButton.IsChecked = muted;
            UpdateMuteButtonVisual();
            _suppressEvents = false;
        }

        public void SetTargetExecutable(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                _audioTargets.Clear();
            }
            else
            {
                _audioTargets = new List<AudioTarget>
                {
                    new AudioTarget { Name = target, IsInputDevice = IsInputMode }
                };
            }

            UpdateTargetsDisplay();
            UpdateMuteButtonEnabled();
        }
        public void SetTargets(List<AudioTarget> targets)
        {
            _audioTargets = targets ?? new List<AudioTarget>();
            UpdateTargetsDisplay();
            UpdateMuteButtonEnabled();
        }

        public void SetVolume(float level)
        {
            _suppressEvents = true; // ✅ prevent events
            VolumeSlider.Value = level;
            _suppressEvents = false;
        }


        public void SmoothAndSetVolume(float rawLevel, bool suppressEvent = false, bool disableSmoothing = false)
        {
            _suppressEvents = suppressEvent;
            if (disableSmoothing)
            {
                SetVolume(rawLevel);
            }
            else
            {
                _smoothedVolume = _smoothedVolume == 0 ? rawLevel : _smoothedVolume + (rawLevel - _smoothedVolume) * SmoothingFactor;
                SetVolume(_smoothedVolume);
            }
            _suppressEvents = false;
        }



        public void UpdateAudioMeter(float rawLevel)
        {
            if (AudioMask.Visibility != Visibility.Visible)
                return;

            _meterLevel += (rawLevel - _meterLevel) * 0.3f;
            double maxHeight = VolumeSlider.ActualHeight > 0 ? VolumeSlider.ActualHeight : 180;

            double maskedHeight = maxHeight * (1 - _meterLevel);
            AudioMask.Height = maskedHeight;

            double peakOffset = maxHeight * _peakLevel;
            PeakHoldBar.Visibility = _peakLevel > 0.01 ? Visibility.Visible : Visibility.Collapsed;
            PeakHoldBar.Margin = new Thickness(0, maxHeight - peakOffset, 0, 0);

            ClipLight.Visibility = rawLevel >= ClipThreshold ? Visibility.Visible : Visibility.Collapsed;

            if (rawLevel > _peakLevel || DateTime.Now - _peakTimestamp > PeakHoldDuration)
            {
                _peakLevel = rawLevel;
                _peakTimestamp = DateTime.Now;
            }
        }

        #endregion Public Methods

        #region Private Methods
        private void UpdateTargetsDisplay()
        {
            if (_audioTargets.Count == 0)
            {
                TargetTextBox.Text = "";
                TargetTextBox.ToolTip = "";
            }
            else if (_audioTargets.Count == 1)
            {
                TargetTextBox.Text = _audioTargets[0].Name;
                TargetTextBox.ToolTip = _audioTargets[0].Name;
            }
            else
            {
                // Show count and first app
                var firstTarget = _audioTargets[0].Name;
                TargetTextBox.Text = $"{firstTarget} +{_audioTargets.Count - 1}";

                // Set tooltip to show all targets
                TargetTextBox.ToolTip = string.Join("\n", _audioTargets.Select(t =>
                    $"{t.Name} {(t.IsInputDevice ? "(Input)" : "")}"));
            }

            UpdateMuteButtonEnabled();
        }
        private void ChannelControl_Loaded(object sender, RoutedEventArgs e)
        {
            _layoutReady = true;
            UpdateMuteButtonVisual();
        }

        private void ChannelControl_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Open the multi-target picker
            var picker = new MultiTargetPickerDialog(_audioTargets)
            {
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            if (picker.Owner != null)
            {
                var window = picker.Owner;
                picker.Left = window.Left + (window.Width - picker.Width) / 2;
                picker.Top = window.Top + (window.Height - picker.Height) / 2;
            }

            if (picker.ShowDialog() == true)
            {
                _audioTargets = picker.SelectedTargets;
                UpdateTargetsDisplay();
                TargetChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        private void InputModeCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            RaiseTargetChanged(); // You already track state via IsInputMode
        }

        private void InputModeCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            RaiseTargetChanged();
        }



    
        private void RaiseTargetChanged()
        {
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }

        private void MuteButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;

            _isMuted = true;
            UpdateMuteButtonVisual();
            VolumeOrMuteChanged?.Invoke(_audioTargets, CurrentVolume, _isMuted);
        }

        private void MuteButton_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;

            _isMuted = false;
            UpdateMuteButtonVisual();
            VolumeOrMuteChanged?.Invoke(_audioTargets, CurrentVolume, _isMuted);
        }

        private void TargetTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateMuteButtonEnabled(); // 👈 keep in sync
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateMuteButtonEnabled()
        {
            MuteButton.IsEnabled = _audioTargets.Count > 0;
        }
        private void UpdateMuteButtonVisual()
        {
            if (MuteButton != null)
            {
                MuteButton.Content = _isMuted ? "Unmute" : "Mute";
                MuteButton.Background = _isMuted ? _muteOnBrush : _muteOffBrush;
                MuteButton.Foreground = Brushes.White;
            }
        }

        #endregion Private Methods

    }
}
