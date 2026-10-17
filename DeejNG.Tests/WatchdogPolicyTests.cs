using DeejNG.Services;
using Xunit;

namespace DeejNG.Tests
{
    /// <summary>
    /// Covers the watchdog rule that a non-empty receive buffer is evidence the link is alive.
    ///
    /// On 2026-10-17 the diagnostic log recorded the watchdog calling SILENT_LINK at
    /// 15:40:46.503 while 7542 bytes were waiting to be read. The buffer size was only ever
    /// consulted to decide whether to send a probe byte, never to judge liveness, so a stalled
    /// read was counted as silence and the port was closed anyway. Closing it dropped the
    /// stream, and reopening it used to assert DTR, which resets the Arduino - it then reported
    /// every slider at zero, and inverted that is 100% on every channel, which is what the
    /// user saw at 15:41 and again at 16:01.
    ///
    /// The same session recorded the three consecutive quiet intervals that led to it, each
    /// with the number of bytes still waiting in the buffer:
    ///
    ///   15:40:44.472  counter 1/3  bytesToRead 5618
    ///   15:40:45.492  counter 2/3  bytesToRead 6518
    ///   15:40:46.503  counter 3/3  bytesToRead 7542 -&gt; disconnect
    ///
    /// None of those three ticks described a dead link, so none of them may disconnect.
    /// </summary>
    public class WatchdogPolicyTests
    {
        private const int MaxQuietIntervals = 3;

        /// <summary>
        /// The recorded stall, tick by tick. The counter is the value <c>CheckConnection</c>
        /// held before the tick, which the log printed as `counter = (value+1)/3`.
        /// </summary>
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
            // A cable that has actually come out produces no bytes at all, and the watchdog
            // must still give up on it at the same point it used to.
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

        /// <summary>
        /// Pending bytes must neither drain-and-probe nor drain-and-disconnect: draining is the
        /// whole answer while there is anything left to read.
        /// </summary>
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
