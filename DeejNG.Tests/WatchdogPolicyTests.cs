using DeejNG.Services;
using Xunit;

namespace DeejNG.Tests
{
    // The 2026-10-17 stall: the watchdog called SILENT_LINK at 15:40:46 while 7542 bytes sat
    // unread. None of the ticks below described a dead link, so none of them may disconnect.
    public class WatchdogPolicyTests
    {
        private const int MaxQuietIntervals = 3;

        // The counter is what CheckConnection held before the tick; the log printed (value+1)/3.
        [Theory]
        [InlineData(5618, 0)]  // 15:40:44.472, log printed counter 1/3
        [InlineData(6518, 1)]  // 15:40:45.492, log printed counter 2/3
        [InlineData(7542, 2)]  // 15:40:46.503, log printed counter 3/3, then disconnect
        public void TheRecordedStallIsDrainedRatherThanDisconnected(int bytesToRead, int noDataCounter)
        {
            var verdict = WatchdogPolicy.Decide(quiet: true, bytesToRead: bytesToRead, noDataCounter: noDataCounter, maxQuietIntervals: MaxQuietIntervals);

            Assert.Equal(WatchdogVerdict.DrainPendingBytes, verdict);
        }

        [Fact]
        public void TheThirdIntervalOfARealOutageStillDisconnects()
        {
            // A cable that is actually out produces no bytes; the watchdog must still give up.
            var verdict = WatchdogPolicy.Decide(quiet: true, bytesToRead: 0, noDataCounter: 2, maxQuietIntervals: MaxQuietIntervals);

            Assert.Equal(WatchdogVerdict.Disconnect, verdict);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void AnEmptyBufferThatIsNotYetExhaustedProbes(int noDataCounter)
        {
            var verdict = WatchdogPolicy.Decide(quiet: true, bytesToRead: 0, noDataCounter: noDataCounter, maxQuietIntervals: MaxQuietIntervals);

            Assert.Equal(WatchdogVerdict.Probe, verdict);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(7542)]
        public void DataWithinTheQuietThresholdEndsTheTick(int bytesToRead)
        {
            var verdict = WatchdogPolicy.Decide(quiet: false, bytesToRead: bytesToRead, noDataCounter: 2, maxQuietIntervals: MaxQuietIntervals);

            Assert.Equal(WatchdogVerdict.Healthy, verdict);
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(1, 1)]
        [InlineData(1, 2)]
        [InlineData(32768, 2)]
        public void PendingBytesAlwaysDrainRegardlessOfTheCounter(int bytesToRead, int noDataCounter)
        {
            var verdict = WatchdogPolicy.Decide(quiet: true, bytesToRead: bytesToRead, noDataCounter: noDataCounter, maxQuietIntervals: MaxQuietIntervals);

            Assert.Equal(WatchdogVerdict.DrainPendingBytes, verdict);
        }
    }
}
