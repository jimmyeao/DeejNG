using DeejNG.Models;
using System.Collections.Generic;

namespace DeejNG.Services
{
    /// <summary>
    /// Pure helpers for reconciling the live slider layout with the slider count reported by the
    /// serial device.
    ///
    /// A payload whose field count differs from the current layout is not trustworthy on its own.
    /// The first line assembled after the port opens can be the tail of a line that began before we
    /// started listening, and <see cref="SerialConnectionManager"/> strips button fields before the
    /// payload reaches the window, so a truncated line can present as a smaller slider count than
    /// the hardware really has.
    ///
    /// Previously a count mismatch rebuilt every channel from the saved profile and persisted the
    /// result immediately, which turned one malformed payload into permanent loss of the user's
    /// channel mappings. These helpers let the caller rebuild without sacrificing existing
    /// mappings, and are kept free of WPF dependencies so they can be tested directly.
    /// </summary>
    public static class SliderLayoutReconciler
    {
        /// <summary>
        /// Determines whether a payload's field count warrants rebuilding the slider layout.
        /// Counts of zero or less are always rejected.
        /// </summary>
        /// <param name="liveCount">Number of channels currently displayed.</param>
        /// <param name="payloadCount">Number of slider values in the incoming payload.</param>
        public static bool IsRebuildWarranted(int liveCount, int payloadCount)
        {
            return payloadCount > 0 && payloadCount != liveCount;
        }

        /// <summary>
        /// Overlays the mappings in use before a layout change onto the channels restored from the
        /// active profile, so a count change never discards a mapping the user configured.
        ///
        /// A channel keeps its previous mapping when it has one. Channels that were previously
        /// unmapped, or that did not exist before, fall back to the profile value. The returned
        /// list always contains exactly <paramref name="channelCount"/> entries.
        /// </summary>
        /// <param name="restoredFromProfile">Targets on the freshly rebuilt channels.</param>
        /// <param name="previousChannels">Targets on the channels as they were before the rebuild.</param>
        /// <param name="channelCount">Number of channels after the rebuild.</param>
        public static List<List<AudioTarget>> ApplyPreviousMappings(
            IReadOnlyList<List<AudioTarget>> restoredFromProfile,
            IReadOnlyList<List<AudioTarget>> previousChannels,
            int channelCount)
        {
            var result = new List<List<AudioTarget>>(channelCount);

            for (int i = 0; i < channelCount; i++)
            {
                var previous = (previousChannels != null && i < previousChannels.Count)
                    ? previousChannels[i]
                    : null;

                if (previous != null && previous.Count > 0)
                {
                    result.Add(previous);
                    continue;
                }

                var restored = (restoredFromProfile != null && i < restoredFromProfile.Count)
                    ? restoredFromProfile[i]
                    : null;

                result.Add(restored ?? new List<AudioTarget>());
            }

            return result;
        }
    }
}
