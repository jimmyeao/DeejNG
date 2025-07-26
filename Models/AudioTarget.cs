using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeejNG.Models
{
    /// <summary>
    /// Represents a target for audio control (e.g., an app session, input device, or output device).
    /// Used to define what a channel or slider will control.
    /// </summary>
    public class AudioTarget
    {
        /// <summary>
        /// The name of the audio target (e.g., "Spotify", "Microphone", "Speakers").
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Indicates whether the target is an input device (e.g., a microphone).
        /// </summary>
        public bool IsInputDevice { get; set; } = false;

        /// <summary>
        /// Indicates whether the target is an output device (e.g., speakers or headphones).
        /// </summary>
        public bool IsOutputDevice { get; set; } = false;
    }

}