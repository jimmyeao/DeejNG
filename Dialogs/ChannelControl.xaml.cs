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
        }

        public void SetVolume(float level)
        {
            VolumeSlider.Value = level;
        }

        public void SmoothAndSetVolume(float rawLevel)
        {
            _smoothedVolume = _smoothedVolume == 0 ? rawLevel : _smoothedVolume + (rawLevel - _smoothedVolume) * SmoothingFactor;
            SetVolume(_smoothedVolume);
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
            var picker = new SessionPickerDialog(TargetTextBox.Text);
            if (picker.ShowDialog() == true)
            {
                SetTargetExecutable(picker.SessionComboBox.Text);
                TargetChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            UpdateMuteButtonVisual();
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }

        private void MuteButton_Checked(object sender, RoutedEventArgs e)
        {
            _isMuted = true;
            UpdateMuteButtonVisual();
        }

        private void MuteButton_Unchecked(object sender, RoutedEventArgs e)
        {
            _isMuted = false;
            UpdateMuteButtonVisual();
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
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion Private Methods
    }
}
