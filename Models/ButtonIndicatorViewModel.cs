using System.ComponentModel;

namespace DeejNG.Models
{
    /// <summary>
    /// View model for button indicator UI.
    /// </summary>
    public class ButtonIndicatorViewModel : INotifyPropertyChanged
    {
        #region Private Fields

        private bool _isPressed;

        #endregion Private Fields

        #region Public Events

        public event PropertyChangedEventHandler? PropertyChanged;

        #endregion Public Events

        #region Public Properties

        public ButtonAction Action { get; set; } = ButtonAction.None;
        public string ActionText { get; set; } = string.Empty;
        /// <summary>
        /// Gets the background color based on button action and pressed state.
        /// Returns color string for pressed state, null for unpressed (uses default).
        /// </summary>
        public string? ActiveBackgroundColor
        {
            get
            {
                if (!IsPressed)
                    return null;

                return Action switch
                {
                    ButtonAction.MuteChannel => "#D13438",      // Red for mute
                    ButtonAction.GlobalMute => "#D13438",       // Red for global mute
                    ButtonAction.MediaPlayPause => "#3B82F6",   // Blue for play/pause
                    ButtonAction.MediaNext => "#10B981",        // Green for next
                    ButtonAction.MediaPrevious => "#10B981",    // Green for previous
                    ButtonAction.MediaStop => "#10B981",        // Green for stop
                    _ => null
                };
            }
        }

        public int ButtonIndex { get; set; }
        public string Icon { get; set; } = string.Empty;
        public bool IsPressed
        {
            get => _isPressed;
            set
            {
                if (_isPressed != value)
                {
                    _isPressed = value;
                    OnPropertyChanged(nameof(IsPressed));
                    OnPropertyChanged(nameof(ActiveBackgroundColor));
                }
            }
        }

        public string Label { get; set; } = string.Empty;
        public string ToolTip { get; set; } = string.Empty;

        #endregion Public Properties

        #region Protected Methods

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion Protected Methods
    }
}
