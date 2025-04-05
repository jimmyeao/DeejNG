using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DeejNG.Dialogs
{
    public partial class ChannelControl : UserControl
    {
        public string TargetExecutable => TargetTextBox.Text;
        public float CurrentVolume => (float)VolumeSlider.Value;
        private float _smoothedVolume;
        private const float SmoothingFactor = 0.1f;
        public event EventHandler TargetChanged;
        private float _meterLevel;
        private float _peakLevel;
        private DateTime _peakTimestamp;
        private const float ClipThreshold = 0.98f;
        private readonly TimeSpan PeakHoldDuration = TimeSpan.FromSeconds(1);
        private bool _layoutReady = false;

        public ChannelControl()
        {
            InitializeComponent();
            Loaded += ChannelControl_Loaded;
        }
        private void TargetTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
        private void ChannelControl_Loaded(object sender, RoutedEventArgs e)
        {
            _layoutReady = true;
        }

        public void UpdateAudioMeter(float rawLevel)
        {
            if (AudioMask.Visibility != Visibility.Visible)
                return;
            _meterLevel += (rawLevel - _meterLevel) * 0.3f;
            // Use fallback height if layout hasn't completed
            double maxHeight = VolumeSlider.ActualHeight > 0 ? VolumeSlider.ActualHeight : 180;

            // Smooth level
            _meterLevel += (rawLevel - _meterLevel) * 0.3f;

            // Update peak hold
            if (rawLevel > _peakLevel || DateTime.Now - _peakTimestamp > PeakHoldDuration)
            {
                _peakLevel = rawLevel;
                _peakTimestamp = DateTime.Now;
            }

            // Meter fill
            double maskedHeight = maxHeight * (1 - _meterLevel);
            AudioMask.Height = maskedHeight;

            // Peak hold bar
            double peakOffset = maxHeight * _peakLevel;
            PeakHoldBar.Visibility = _peakLevel > 0.01 ? Visibility.Visible : Visibility.Collapsed;
            PeakHoldBar.Margin = new Thickness(0, maxHeight - peakOffset, 0, 0);

            // Clip light
            ClipLight.Visibility = rawLevel >= ClipThreshold ? Visibility.Visible : Visibility.Collapsed;
        }
        public void SetMeterVisibility(bool visible)
        {
            MeterVisuals.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }







        public void SetVolume(float level)
        {
            VolumeSlider.Value = level;
           // LevelMeter.Value = level;
        }
        public void SetTargetExecutable(string target)
        {
            TargetTextBox.Text = target;
        }
        public void SmoothAndSetVolume(float rawLevel)
        {
            _smoothedVolume = _smoothedVolume == 0 ? rawLevel : _smoothedVolume + (rawLevel - _smoothedVolume) * SmoothingFactor;
            SetVolume(_smoothedVolume);
        }

    }
}
