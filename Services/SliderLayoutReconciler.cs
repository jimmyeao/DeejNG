using DeejNG.Models;
using System.Collections.Generic;

namespace DeejNG.Services
{
    // A count mismatch used to rebuild every channel from the profile and save immediately,
    // so one malformed payload permanently destroyed the user's channel mappings.
    public static class SliderLayoutReconciler
    {
        public static bool IsRebuildWarranted(int liveCount, int payloadCount)
        {
            return payloadCount > 0 && payloadCount != liveCount;
        }

        // A channel keeps its previous mapping; unmapped or brand-new channels fall back
        // to the profile value.
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
