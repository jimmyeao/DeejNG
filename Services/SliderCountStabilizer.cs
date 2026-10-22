using System;

namespace DeejNG.Services
{
    // A real layout change reports the new count on every frame; a damaged frame is a one-off
    // surrounded by correct ones. Time is injected so tests can replay the recorded storm.
    public sealed class SliderCountStabilizer
    {
        // Under 200ms at the ~19 frames a second a 9600 baud link sustains.
        public const int DefaultRequiredFrames = 3;

        // A damaged burst never holds one count this long.
        public static readonly TimeSpan DefaultRequiredDuration = TimeSpan.FromMilliseconds(300);

        private readonly int _requiredFrames;
        private readonly TimeSpan _requiredDuration;
        private int _liveCount;
        private int _candidateCount = NoCandidate;
        private int _observedFrames;
        private DateTime _firstObservedAt;

        private const int NoCandidate = -1;

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

        public int CandidateCount => _candidateCount;

        public int ObservedFrames => _observedFrames;

        // Returns true at most once per candidate; the caller rebuilds, then calls Reset.
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
