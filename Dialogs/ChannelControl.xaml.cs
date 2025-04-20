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
        private readonly TimeSpan PeakHoldDuration = TimeSpan.FromSeconds(1);
        private bool _isMuted = false;
        private bool _layoutReady = false;
        private float _meterLevel;
        private float _peakLevel;
        private DateTime _peakTimestamp;
        private float _smoothedVolume;
        public event Action<string, float, bool> VolumeOrMuteChanged;
        private bool _suppressEvents = false;
        private readonly Brush _muteOnBrush = new SolidColorBrush(Color.FromRgb(255, 64, 64)); // Bright red
        private readonly Brush _muteOffBrush = Brushes.Gray;
      
        #endregion Private Fields

        #region Public Constructors

        public ChannelControl()
        {
            InitializeComponent();
            Loaded += ChannelControl_Loaded;
            MouseDoubleClick += ChannelControl_MouseDoubleClick;
        }

        #endregion Public Constructors

        #region Public Events

        public event EventHandler TargetChanged;

        #endregion Public Events

        #region Public Properties

        public float CurrentVolume => (float)VolumeSlider.Value;
        public bool IsMuted => _isMuted;
        public string TargetExecutable => TargetTextBox.Text;

        #endregion Public Properties

        #region Public Methods

        public void SetMeterVisibility(bool visible)
        {
            MeterVisuals.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetTargetExecutable(string target)
        {
            TargetTextBox.Text = target;
            UpdateMuteButtonEnabled(); // 👈 now it disables the mute button if empty
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

        private void ChannelControl_Loaded(object sender, RoutedEventArgs e)
        {
            _layoutReady = true;
            UpdateMuteButtonVisual();
        }

        private void ChannelControl_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var picker = new SessionPickerDialog(TargetTextBox.Text)
            {
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            if (picker.Owner != null)
            {
                var mainWindow = picker.Owner;
                picker.Left = mainWindow.Left + (mainWindow.Width - picker.Width) / 2;
                picker.Top = mainWindow.Top + (mainWindow.Height - picker.Height) / 2;
            }

            if (picker.ShowDialog() == true)
            {
                SetTargetExecutable(picker.SessionComboBox.Text);
                TargetChanged?.Invoke(this, EventArgs.Empty);
            }
        }


        private void UpdateMuteButtonEnabled()
        {
            var target = TargetExecutable?.Trim();
            MuteButton.IsEnabled = !string.IsNullOrWhiteSpace(target) && !string.Equals(target, "(empty)", StringComparison.OrdinalIgnoreCase);
        }



        private void MuteButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;

            _isMuted = true;
            UpdateMuteButtonVisual();
            VolumeOrMuteChanged?.Invoke(TargetExecutable, CurrentVolume, _isMuted);
        }

        private void MuteButton_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;

            _isMuted = false;
            UpdateMuteButtonVisual();
            VolumeOrMuteChanged?.Invoke(TargetExecutable, CurrentVolume, _isMuted);
        }
        public void SetMuted(bool muted)
        {
            _suppressEvents = true;
            _isMuted = muted;
            MuteButton.IsChecked = muted;
            UpdateMuteButtonVisual();
            _suppressEvents = false;
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

        private void TargetTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateMuteButtonEnabled(); // 👈 keep in sync
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }


        #endregion Private Methods
    }
}
