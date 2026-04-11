using System.Collections.Generic;

namespace DeejNG.Models
{
    public enum PickerCategory { Apps, Inputs, Outputs }

    /// <summary>
    /// Tracks the state of the hardware encoder picker for one channel.
    /// Created when a picker opens; discarded when it closes.
    /// </summary>
    public class ChannelPickerState
    {
        /// <summary>0-based index of the channel whose picker is open.</summary>
        public int ChannelIndex { get; set; }

        /// <summary>Volume (0–100) captured when the picker opened, used to restore on close.</summary>
        public int SnapshotVol { get; set; }

        /// <summary>Mute state captured when the picker opened, used to restore on close.</summary>
        public bool SnapshotMuted { get; set; }

        /// <summary>Which category of targets the list currently shows.</summary>
        public PickerCategory Category { get; set; } = PickerCategory.Apps;

        /// <summary>Full item list for the current category.</summary>
        public List<AudioTarget> Items { get; set; } = new();

        /// <summary>Index into Items that is currently highlighted (0-based).</summary>
        public int SelectedIndex { get; set; }
    }
}
