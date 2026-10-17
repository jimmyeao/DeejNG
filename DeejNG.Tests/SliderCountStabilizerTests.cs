using DeejNG.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace DeejNG.Tests
{
    /// <summary>
    /// Covers the guard that stops a damaged serial frame from being mistaken for a hardware
    /// layout change.
    ///
    /// On 2026-10-17 the reporter's machine was under game load, the serial reader was starved,
    /// the receive buffer overran, and the damaged bytes that came back produced frames of 1, 2,
    /// 3, 4, 6, 7, 8, 9, 13 and 15 fields. Every one rebuilt the whole slider panel, which cost
    /// about 600ms of UI time, which starved the reader further. The storm ran for 29 seconds and
    /// left the panel showing whatever count the last damaged frame happened to have — the user
    /// saw one slider, then eight.
    ///
    /// The payloads in <see cref="RecordedStormPayloads"/> are those frames in the order the
    /// diagnostic log recorded them.
    /// </summary>
    public class SliderCountStabilizerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 17, 14, 29, 47);

        /// <summary>One frame interval at 9600 baud with a five-slider sketch.</summary>
        private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(52);

        private const int HealthyFrameFields = 5;

        /// <summary>
        /// Every slider payload MainWindow was handed during the rebuild storm, in order, exactly
        /// as the diagnostic log recorded them in the `payload=` field of each REBUILD line.
        /// Entries with five fields are the healthy frames the device kept sending, which the log
        /// recorded whenever a previous rebuild had left the live count at something else.
        /// </summary>
        private static readonly string[] RecordedStormPayloads =
        {
            "327",                                                             // first line after the DTR reset
            "327|0|0|0|0",
            "34|745|244|227|432|1",
            "34|745|244|227|432",
            "34|745|2|244|227|432",
            "34|745|244|227|432",
            "34|745|244|2|227|432",
            "34|745|244|227|432|100|227|432",
            "34|745|244|227|432",
            "34|700",
            "34|745|244|227|432",
            "34|745|244|227",
            "34|745|244|227|432",
            "34|745|244|2",
            "34|745|244|227|432",
            "34",
            "34|244|227|432",
            "34|745|244|227|432",
            "34|745|244|227|432|144|227|432",                                  // the "8 channel" the user saw
            "34|745|244|227|432",
            "34",
            "34|745|244|227|432",
            "34|745|244|227|432|244|227|432",                                  // the second "8 channel"
            "34|745|244|227|432",
            "34|745|244|227|7|432",
            "34|745|244|227|432",
            "34|745|244|227|432|000",
            "34|745|244|227|432",
            "34|74",
            "34|745|244|227|432",
            "34|745|244|227|432|5|244|227|432",
            "34|745|244|227|432",
            "34|745|244|227|432|0",
            "34|745|244|227|432",
            "34|0",
            "34|745|244|227|432",
            "34|0",
            "34|745|244|227|432",
            "34|0",
            "34|745|244|227|432",
            "34|0",
            "34|745|244|227|432",
            "34|0",
            "34|745|244|227|432",
            "34|0",
            "34|745|244|227|432",
            "34|745|244|2745|244|2745|244|2745|244|2745|244|2745|244|227|432", // digits repeated down the frame
            "34|745|244|227|432",
            "34|745|244|227|432|1000",
            "34|745|244|227|432",
            "3",
            "34|745|244|227|432",
            "34|745|244",
            "34|745|244|227|432",
            "34|745|244",
            "34|745|244|227|432",
            "34|745|244",
            "34|745|244|227|432",
            "34|745|244|227|432|100",
            "44|227|432",
            "34|745|244|227|432",
            "227|432",
            "34|745|244|227|432",
            "34|745|244|227|432|100",
            "34|745|244|227|432",
            "3000",
            "34|745|244|227|432",
            "3000",
            "34|745|244|227|432",
            "3000",
            "34|745|244|227|432",
            "34|745|4|745|244|227|432",
            "34|745|227|432",
            "34|745|244|227|432",
            "34|745|244|227|432|227|432",
            "34|745|244|227|432",
            "34|745|244|227|432|1000",
            "34|745|244|227|432"
        };

        /// <summary>
        /// Runs the recorded payloads through the same decision MainWindow makes, returning how
        /// many rebuilds it performed, the channel count it ended on, and how many frames asked
        /// for a rebuild outright (which is what the guard exists to refuse).
        /// </summary>
        private static (int Rebuilds, int Triggers, int Live) Replay(
            IEnumerable<string> payloads, int initialLive = 5)
        {
            var stabilizer = new SliderCountStabilizer(initialLive);
            var now = T0;
            int live = initialLive;
            int rebuilds = 0;
            int triggers = 0;

            foreach (var payload in payloads)
            {
                int count = payload.Split('|').Length;

                if (!SliderLayoutReconciler.IsRebuildWarranted(live, count))
                {
                    // A frame that agrees with the layout contradicts any pending change.
                    stabilizer.Reset(live);
                }
                else if (stabilizer.Accepts(count, now))
                {
                    live = count;
                    stabilizer.Reset(live);
                    rebuilds++;
                }
                else
                {
                    triggers++;
                }

                now = now + FrameInterval;
            }

            return (rebuilds, triggers, live);
        }

        [Fact]
        public void ReplayingTheRecordedStorm_RebuildsNothing()
        {
            var (rebuilds, triggers, live) = Replay(RecordedStormPayloads);

            // The recording really does demand a rebuild over and over; the zero below is the
            // guard refusing all of them, not a scenario with nothing to refuse.
            Assert.True(triggers >= 30, $"only {triggers} frames asked for a rebuild");
            Assert.Equal(0, rebuilds);
            Assert.Equal(5, live);
        }

        [Fact]
        public void ReplayingOnlyTheDamagedFrames_RebuildsNothing()
        {
            // Worst case: the healthy frames the log also recorded are removed, so the damaged
            // counts sit back to back with nothing to contradict them.
            var damaged = RecordedStormPayloads
                .Where(p => p.Split('|').Length != HealthyFrameFields);

            var (rebuilds, triggers, live) = Replay(damaged);

            Assert.True(triggers >= 30, $"only {triggers} frames asked for a rebuild");
            Assert.Equal(0, rebuilds);
            Assert.Equal(5, live);
        }

        [Fact]
        public void ASingleDisagreeingFrame_IsHeld()
        {
            var stabilizer = new SliderCountStabilizer(5);

            Assert.False(stabilizer.Accepts(1, T0));
        }

        [Fact]
        public void ACountRepeatedButNotSustained_IsHeld()
        {
            var stabilizer = new SliderCountStabilizer(5);
            var now = T0;

            // The longest run of one damaged count in the recording: six two-field frames back to
            // back. That spans 260ms, which is still inside the stability window.
            for (int i = 0; i < 6; i++)
            {
                Assert.False(stabilizer.Accepts(2, now));
                now = now + FrameInterval;
            }
        }

        [Fact]
        public void ACountSustainedLongEnough_IsAccepted()
        {
            var stabilizer = new SliderCountStabilizer(5);

            Assert.False(stabilizer.Accepts(6, T0));
            Assert.False(stabilizer.Accepts(6, T0.AddMilliseconds(52)));
            Assert.False(stabilizer.Accepts(6, T0.AddMilliseconds(104))); // three frames, only 104ms
            Assert.False(stabilizer.Accepts(6, T0.AddMilliseconds(260))); // still inside the window
            Assert.True(stabilizer.Accepts(6, T0.AddMilliseconds(312)));
        }

        [Fact]
        public void AHealthyFrameBetweenDisagreeingOnes_ClearsTheCandidate()
        {
            var stabilizer = new SliderCountStabilizer(5);

            // Damaged, healthy, damaged, healthy... never the three in a row a change needs, even
            // though each damaged pair here is seconds apart.
            for (int i = 0; i < 10; i++)
            {
                Assert.False(stabilizer.Accepts(8, T0.AddMilliseconds(i * 500)));
                stabilizer.Reset(5);
            }
        }

        [Fact]
        public void Reset_DiscardsTheCandidate()
        {
            var stabilizer = new SliderCountStabilizer(5);

            stabilizer.Accepts(6, T0);
            stabilizer.Accepts(6, T0.AddMilliseconds(52));
            Assert.Equal(6, stabilizer.CandidateCount);

            stabilizer.Reset(5);

            Assert.Equal(-1, stabilizer.CandidateCount);
            Assert.Equal(0, stabilizer.ObservedFrames);

            // The count has to be earned again from scratch.
            Assert.False(stabilizer.Accepts(6, T0.AddMilliseconds(104)));
        }

        [Fact]
        public void TheLiveCount_IsNeverACandidate()
        {
            var stabilizer = new SliderCountStabilizer(5);

            for (int i = 0; i < 10; i++)
            {
                Assert.False(stabilizer.Accepts(5, T0.AddSeconds(i)));
            }

            Assert.Equal(-1, stabilizer.CandidateCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void NonPositiveCounts_AreNeverAccepted(int payloadCount)
        {
            var stabilizer = new SliderCountStabilizer(5);

            for (int i = 0; i < 10; i++)
            {
                Assert.False(stabilizer.Accepts(payloadCount, T0.AddSeconds(i)));
            }
        }

        [Fact]
        public void RequiredFramesMustBeAtLeastOne()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SliderCountStabilizer(5, 0));
        }

        [Fact]
        public void TheGuardStillLetsARealLayoutChangeThrough()
        {
            // The counterpart to the storm tests: a device that really did change keeps reporting
            // the new count, so it is still picked up, about 300ms later.
            var (rebuilds, triggers, live) = Replay(
                Enumerable.Repeat("34|745|244|227|432|0", 40),
                initialLive: 5);

            Assert.Equal(1, rebuilds);
            Assert.Equal(6, triggers); // the six frames before the change was confirmed
            Assert.Equal(6, live);
        }
    }
}
