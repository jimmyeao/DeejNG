using System;

namespace DeejNG.Services
{
    /// <summary>
    /// Holds back a slider-count change reported by the device until the device has repeated the
    /// new count on consecutive frames and stuck with it.
    ///
    /// A real layout change is not a one-off: firmware that lost or gained a slider reports the
    /// new count on every frame from then on. A damaged frame from an overrun link is the
    /// opposite — a single disagreeing count surrounded by correct ones. Rebuilding the panel for
    /// each of those turned a burst of damaged data into a rebuild storm that saturated the UI
    /// thread for about thirty seconds, which is what the user saw as the app hanging and then
    /// coming back with the wrong number of sliders.
    ///
    /// Time is passed in on every call rather than read from the clock so the behaviour is
    /// directly testable against a recording of the real storm.
    /// </summary>
    public sealed class SliderCountStabilizer
    {
        /// <summary>
        /// Frames the new count must appear on before it is believed. Three frames is well under
        /// 200ms at the ~19 frames a second a 9600 baud link sustains, so a genuine change still
        /// lands imperceptibly quickly.
        /// </summary>
        public const int DefaultRequiredFrames = 3;

        /// <summary>
        /// How long the new count must be held. Together with the frame count this means a
        /// damaged frame has to be followed by several more carrying the same wrong count before
        /// anything is rebuilt, which a burst of differently-shaped frames never achieves.
        /// </summary>
        public static readonly TimeSpan DefaultRequiredDuration = TimeSpan.FromMilliseconds(300);

        private readonly int _requiredFrames;
        private readonly TimeSpan _requiredDuration;
        private int _liveCount;
        private int _candidateCount = NoCandidate;
        private int _observedFrames;
        private DateTime _firstObservedAt;

        private const int NoCandidate = -1;

        /// <param name="liveCount">Channels currently on screen.</param>
        /// <param name="requiredFrames">Consecutive frames the new count must appear on.</param>
        /// <param name="requiredDuration">How long the new count must be held.</param>
        public SliderCountStabilizer(
            int liveCount,
            int requiredFrames = DefaultRequiredFrames,
            TimeSpan? requiredDuration = null)
        {
            if (requiredFrames < 1)
                throw new ArgumentOutOfRangeException(nameof(requiredFrames));

            _requiredFrames = requiredFrames;
            _requiredDuration = requiredDuration ?? DefaultRequiredDuration;
            _liveCount = liveCount;
        }

        /// <summary>Count currently being considered, or -1 when there is none.</summary>
        public int CandidateCount => _candidateCount;

        /// <summary>
        /// How many consecutive frames the candidate has appeared on. Callers can use this to
        /// report only the first frame of a candidate instead of every frame of a burst.
        /// </summary>
        public int ObservedFrames => _observedFrames;

        /// <summary>
        /// Records a frame whose field count disagrees with the live layout and reports whether
        /// the count has now been seen often enough, and held long enough, to be believed.
        ///
        /// Returns true at most once per candidate; the caller is expected to rebuild and then
        /// call <see cref="Reset"/> with the new count.
        /// </summary>
        public bool Accepts(int payloadCount, DateTime now)
        {
            if (payloadCount <= 0 || payloadCount == _liveCount)
            {
                ClearCandidate();
                return false;
            }

            if (payloadCount != _candidateCount)
            {
                _candidateCount = payloadCount;
                _observedFrames = 1;
                _firstObservedAt = now;
                return false;
            }

            _observedFrames++;

            return _observedFrames >= _requiredFrames
                && (now - _firstObservedAt) >= _requiredDuration;
        }

        /// <summary>
        /// Puts the stabilizer back in step with the layout on screen and discards any candidate,
        /// so a burst of damaged frames cannot accumulate into a change.
        /// </summary>
        public void Reset(int liveCount)
        {
            _liveCount = liveCount;
            ClearCandidate();
        }

        private void ClearCandidate()
        {
            _candidateCount = NoCandidate;
            _observedFrames = 0;
            _firstObservedAt = default;
        }
    }
}
