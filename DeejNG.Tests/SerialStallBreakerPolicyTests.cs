using DeejNG.Services;
using Xunit;

namespace DeejNG.Tests
{
    /// <summary>
    /// Covers the circuit breaker that cycles the port when the read path is jammed.
    ///
    /// On 2026-10-17 a thread dump taken during a live stall showed the drain thread
    /// parked inside SerialPort.ReadExisting -> SerialStream.EndRead while holding
    /// SerialPort's internal lock, with eight threadpool threads queued behind that lock
    /// in CatchReceivedEvents. The watchdog still saw the buffer (BytesToRead needs no
    /// lock) and drained each tick, but every drain was a no-op because the drain guard
    /// was held by the stuck thread. The log recorded the byte count pinned at exactly
    /// 32706 for minutes (19:06 window, quiet 219-242 s): a deej board streams
    /// continuously, so a byte count that never moves while the watchdog drains means
    /// nothing is consuming the buffer, and the only remedy left is to close and reopen
    /// the port - which no longer resets the board, because DTR is held low at open.
    /// </summary>
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
            // A drain that actually consumed something - or fresh bytes from the board -
            // both prove the buffer is moving, which is all the reset needs.
            var (ticks, stalled) = SerialStallBreakerPolicy.Advance(pinnedTicks: 2, previousBytes: Pinned, bytesToRead: Pinned - 800);

            Assert.False(stalled);
            Assert.Equal(0, ticks);
        }

        [Fact]
        public void AnEmptyBufferResetsTheTracker()
        {
            // An empty buffer on a quiet tick is WatchdogPolicy's Probe/Disconnect case,
            // not a jam: the tracker must not survive across it.
            var (ticks, stalled) = SerialStallBreakerPolicy.Advance(pinnedTicks: 2, previousBytes: Pinned, bytesToRead: 0);

            Assert.False(stalled);
            Assert.Equal(0, ticks);
        }

        [Fact]
        public void TrackingRestartsFromZeroAfterTheLinkRecovers()
        {
            // Jam (one tick short), recover, jam again: the second jam must count its own
            // ticks rather than inherit the first jam's.
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
