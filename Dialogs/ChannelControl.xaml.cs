using System.Windows;
using System.Windows.Controls;

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
      
        public ChannelControl()
        {
            InitializeComponent();
        }
        private void TargetTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
        public void UpdateAudioMeter(float rawLevel)
        {
            if (VolumeSlider.ActualHeight < 1)
                return;

            // Smooth level
            _meterLevel += (rawLevel - _meterLevel) * 0.3f;

            // Update peak hold
            if (rawLevel > _peakLevel || DateTime.Now - _peakTimestamp > PeakHoldDuration)
            {
                _peakLevel = rawLevel;
                _peakTimestamp = DateTime.Now;
            }

            double maxHeight = VolumeSlider.ActualHeight;

            // Meter fill (mask shrinks to reveal color)
            double maskedHeight = maxHeight * (1 - _meterLevel);
            AudioMask.Height = maskedHeight;

            // Peak bar position
            double peakOffset = maxHeight * _peakLevel;
            PeakHoldBar.Visibility = _peakLevel > 0.01 ? Visibility.Visible : Visibility.Collapsed;
            PeakHoldBar.Margin = new Thickness(0, maxHeight - peakOffset, 0, 0);

            // Clip indicator
            ClipLight.Visibility = rawLevel >= ClipThreshold ? Visibility.Visible : Visibility.Collapsed;
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
