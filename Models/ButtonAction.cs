namespace DeejNG.Models
{
    /// <summary>
    /// Defines the action types that can be assigned to a physical button.
    /// </summary>
    public enum ButtonAction
    {
        /// <summary>
        /// No action assigned.
        /// </summary>
        None = 0,

        /// <summary>
        /// Toggle play/pause for media playback.
        /// </summary>
        MediaPlayPause = 1,

        /// <summary>
        /// Skip to next track.
        /// </summary>
        MediaNext = 2,

        /// <summary>
        /// Skip to previous track.
        /// </summary>
        MediaPrevious = 3,

        /// <summary>
        /// Stop media playback.
        /// </summary>
        MediaStop = 4,

        /// <summary>
        /// Toggle mute for a specific channel.
        /// </summary>
        MuteChannel = 5,

        /// <summary>
        /// Toggle global mute (all channels).
        /// </summary>
        GlobalMute = 6,

        /// <summary>
        /// Toggle input/output mode for a specific channel.
        /// </summary>
        ToggleInputOutput = 7
    }
}
