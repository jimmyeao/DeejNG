using System.ComponentModel;

namespace DeejNG.Models
{
    /// <summary>
    /// View model for button indicator UI.
    /// </summary>
    public class ButtonIndicatorViewModel : INotifyPropertyChanged
    {
        private bool _isPressed;

        public int ButtonIndex { get; set; }
        public string Label { get; set; } = string.Empty;
        public string ActionText { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string ToolTip { get; set; } = string.Empty;

        public bool IsPressed
        {
            get => _isPressed;
            set
            {
                if (_isPressed != value)
                {
                    _isPressed = value;
                    OnPropertyChanged(nameof(IsPressed));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
