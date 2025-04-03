using System.Windows.Controls;

namespace DeejNG.Dialogs
{
    public partial class ChannelControl : UserControl
    {
        public string TargetExecutable => TargetTextBox.Text;
        public float CurrentVolume => (float)VolumeSlider.Value;
        private float _smoothedVolume;
        private const float SmoothingFactor = 0.1f;
        public ChannelControl()
        {
            InitializeComponent();
        }

        public void SetVolume(float level)
        {
            VolumeSlider.Value = level;
            LevelMeter.Value = level;
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
