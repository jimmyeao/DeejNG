using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DeejNG.Models
{
    /// <summary>
    /// A single action with its parameters. Used for both the short-press and long-press
    /// action of a button.
    /// </summary>
    public class ButtonActionBinding
    {
        /// <summary>
        /// Gets or sets the action to perform.
        /// </summary>
        public ButtonAction Action { get; set; } = ButtonAction.None;

        /// <summary>
        /// Gets or sets the target channel index for channel-specific actions (0-based).
        /// -1 indicates no specific channel (for global actions).
        /// </summary>
        public int TargetChannelIndex { get; set; } = -1;

        /// <summary>
        /// Key combination for <see cref="ButtonAction.SendKeys"/>, e.g. "Ctrl+Shift+M".
        /// Multiple combos can be sent in sequence by separating them with commas ("Ctrl+K, Ctrl+C").
        /// </summary>
        public string KeyCombo { get; set; } = string.Empty;

        /// <summary>
        /// Command line for <see cref="ButtonAction.RunCommand"/>, executed via cmd.exe /c.
        /// </summary>
        public string Command { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsAssigned => Action != ButtonAction.None;
    }

    /// <summary>
    /// Alternate audio targets for one channel while a button is held ("shift layer").
    /// </summary>
    public class ButtonLayerChannel
    {
        /// <summary>
        /// Gets or sets the channel (slider) index, 0-based.
        /// </summary>
        public int ChannelIndex { get; set; }

        /// <summary>
        /// Gets or sets the targets the slider controls while the button is held.
        /// </summary>
        public List<AudioTarget> Targets { get; set; } = new();
    }

    /// <summary>
    /// Represents the mapping configuration for a physical button.
    /// The inherited action properties describe the short press (or the plain press when
    /// neither a long-press action nor a hold layer is configured).
    /// </summary>
    public class ButtonMapping : ButtonActionBinding
    {
        /// <summary>
        /// Gets or sets the button index (0-based).
        /// </summary>
        public int ButtonIndex { get; set; }

        /// <summary>
        /// Gets or sets a friendly name for this button mapping.
        /// </summary>
        public string FriendlyName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the action executed when the button is held longer than the long-press threshold.
        /// </summary>
        public ButtonActionBinding LongPress { get; set; } = new();

        /// <summary>
        /// Gets or sets the action executed when the button is pressed twice in quick succession.
        /// </summary>
        public ButtonActionBinding DoublePress { get; set; } = new();

        /// <summary>
        /// Gets or sets the channels that switch to alternate targets while this button is held.
        /// </summary>
        public List<ButtonLayerChannel> HoldLayer { get; set; } = new();

        [JsonIgnore]
        public bool HasLongPress => LongPress?.IsAssigned == true;

        [JsonIgnore]
        public bool HasDoublePress => DoublePress?.IsAssigned == true;

        [JsonIgnore]
        public bool HasHoldLayer => HoldLayer?.Any(l => l.ChannelIndex >= 0 && l.Targets?.Count > 0) == true;

        [JsonIgnore]
        public bool HasAnything => IsAssigned || HasLongPress || HasDoublePress || HasHoldLayer;
    }
}
