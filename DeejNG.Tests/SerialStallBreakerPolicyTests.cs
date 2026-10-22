using DeejNG.Services;
using Xunit;

namespace DeejNG.Tests
{
    // The 2026-10-17 thread dump caught the drain parked inside SerialPort.ReadExisting with
    // the port's lock held: the receive count sat pinned at 32706 while every drain no-op'd.
    public class SerialStallBreakerPolicyTests
    {
        private const int Pinned = 32706;

        [Fact]
        public void TheFirstQuietSightingStartsTrackingWithoutStalling()
        {
            var (ticks, stalled) = SerialStallBreakerPolicy.Advance(pinnedTicks: 0, previousBytes: -1, bytesToRead: Pinned);

            Assert.False(stalled);
            Assert.Equal(0, ticks);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 2)]
        public void APinnedCountBelowTheThresholdDoesNotStall(int pinnedTicksSoFar, int expectedTicks)
        {
            var (ticks, stalled) = SerialStallBreakerPolicy.Advance(pinnedTicks: pinnedTicksSoFar, previousBytes: Pinned, bytesToRead: Pinned);

            Assert.False(stalled);
            Assert.Equal(expectedTicks, ticks);
        }

        [Fact]
        public void TheThirdConsecutivePinnedTickCyclesTheLink()
        {
            var (ticks, stalled) = SerialStallBreakerPolicy.Advance(pinnedTicks: 2, previousBytes: Pinned, bytesToRead: Pinned);

            Assert.True(stalled);
            Assert.Equal(3, ticks);
        }

        [Fact]
        public void AMovingByteCountResetsTheTracker()
        {
            // Movement in either direction proves the buffer is alive.
            var (ticks, stalled) = SerialStallBreakerPolicy.Advance(pinnedTicks: 2, previousBytes: Pinned, bytesToRead: Pinned - 800);

            Assert.False(stalled);
            Assert.Equal(0, ticks);
        }

        [Fact]
        public void AnEmptyBufferResetsTheTracker()
        {
            // An empty buffer is WatchdogPolicy's case, not a jam.
            var (ticks, stalled) = SerialStallBreakerPolicy.Advance(pinnedTicks: 2, previousBytes: Pinned, bytesToRead: 0);

            Assert.False(stalled);
            Assert.Equal(0, ticks);
        }

        [Fact]
        public void TrackingRestartsFromZeroAfterTheLinkRecovers()
        {
            // The second jam counts its own ticks, not the first jam's.
            var (ticksAfterFirstJam, _) = SerialStallBreakerPolicy.Advance(0, Pinned, Pinned);
            Assert.Equal(1, ticksAfterFirstJam);

            var (recovered, _) = SerialStallBreakerPolicy.Advance(ticksAfterFirstJam, Pinned, Pinned - 800);
            Assert.Equal(0, recovered);

            var (ticksAfterSecondJam, stalledAgain) = SerialStallBreakerPolicy.Advance(recovered, Pinned - 800, Pinned - 800);
            Assert.Equal(1, ticksAfterSecondJam);
            Assert.False(stalledAgain);
        }
    }
}
