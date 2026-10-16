using DeejNG.Models;
using DeejNG.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace DeejNG.Tests
{
    /// <summary>
    /// Covers the merge policy used when the serial device reports a different slider count than
    /// the layout currently on screen. The regression these pin is jimmyeao/DeejNG#125, where a
    /// single short payload permanently destroyed the user's channel mappings.
    /// </summary>
    public class SliderLayoutReconcilerTests
    {
        private static List<AudioTarget> Targets(params string[] names)
        {
            return names.Select(n => new AudioTarget { Name = n }).ToList();
        }

        private static List<AudioTarget> Unmapped() => new List<AudioTarget>();

        private static string[] Describe(List<List<AudioTarget>> groups)
        {
            return groups
                .Select(g => g.Count == 0 ? "(unmapped)" : string.Join("+", g.Select(t => t.Name)))
                .ToArray();
        }

        // The five mappings from the reporter's "Default" profile.
        private static List<List<AudioTarget>> FullProfile()
        {
            return new List<List<AudioTarget>>
            {
                Targets("H24G30F"),
                Targets("Speakers (Realtek)"),
                Targets("Game (Elgato)"),
                Targets("Browser (Elgato)"),
                Targets("Music (Elgato)")
            };
        }

        [Fact]
        public void ShrinkToOneChannel_KeepsTheFirstChannelsMapping()
        {
            var restored = new List<List<AudioTarget>> { Targets("H24G30F") };
            var previous = FullProfile();

            var result = SliderLayoutReconciler.ApplyPreviousMappings(restored, previous, 1);

            Assert.Equal(new[] { "H24G30F" }, Describe(result));
        }

        [Fact]
        public void GrowingBackFromOneChannel_RestoresEveryMappingFromTheProfile()
        {
            var restored = FullProfile();
            var previous = new List<List<AudioTarget>> { Targets("H24G30F") };

            var result = SliderLayoutReconciler.ApplyPreviousMappings(restored, previous, 5);

            Assert.Equal(
                new[]
                {
                    "H24G30F",
                    "Speakers (Realtek)",
                    "Game (Elgato)",
                    "Browser (Elgato)",
                    "Music (Elgato)"
                },
                Describe(result));
        }

        [Fact]
        public void UnmappedChannel_DoesNotOverwriteTheProfilesMapping()
        {
            var restored = FullProfile();
            var previous = Enumerable.Range(0, 5).Select(_ => Unmapped()).ToList();

            var result = SliderLayoutReconciler.ApplyPreviousMappings(restored, previous, 5);

            Assert.Equal(Describe(FullProfile()), Describe(result));
        }

        [Fact]
        public void ChannelBeyondThePreviousCount_TakesTheProfileValue()
        {
            var restored = FullProfile();
            var previous = new List<List<AudioTarget>> { Targets("H24G30F") };

            var result = SliderLayoutReconciler.ApplyPreviousMappings(restored, previous, 5);

            Assert.Equal("Music (Elgato)", result[4].Single().Name);
        }

        [Fact]
        public void MappingOnAPreviouslyUnmappedChannel_TakesPrecedenceOverTheProfile()
        {
            var restored = new List<List<AudioTarget>>
            {
                Targets("H24G30F"),
                Unmapped()
            };
            var previous = new List<List<AudioTarget>>
            {
                Unmapped(),
                Targets("Discord")
            };

            var result = SliderLayoutReconciler.ApplyPreviousMappings(restored, previous, 2);

            Assert.Equal(new[] { "H24G30F", "Discord" }, Describe(result));
        }

        [Fact]
        public void Result_AlwaysHasExactlyTheRequestedChannelCount()
        {
            foreach (int count in new[] { 1, 3, 5, 8 })
            {
                var result = SliderLayoutReconciler.ApplyPreviousMappings(FullProfile(), FullProfile(), count);

                Assert.Equal(count, result.Count);
            }
        }

        [Fact]
        public void ProfileShorterThanTheChannelCount_FillsTheSurplusWithUnmappedChannels()
        {
            var restored = new List<List<AudioTarget>> { Targets("H24G30F") };

            var result = SliderLayoutReconciler.ApplyPreviousMappings(restored, UnmappedList(), 3);

            Assert.Equal(new[] { "H24G30F", "(unmapped)", "(unmapped)" }, Describe(result));
        }

        [Fact]
        public void NullPreviousChannels_FallsBackToTheProfileValues()
        {
            var result = SliderLayoutReconciler.ApplyPreviousMappings(FullProfile(), null!, 5);

            Assert.Equal(Describe(FullProfile()), Describe(result));
        }

        [Fact]
        public void NullProfileValues_StillProducesOneEntryPerChannel()
        {
            var result = SliderLayoutReconciler.ApplyPreviousMappings(null!, null!, 2);

            Assert.Equal(2, result.Count);
            Assert.All(result, group => Assert.Empty(group));
            Assert.All(result, group => Assert.NotNull(group));
        }

        [Theory]
        [InlineData(5, 5, false)] // stable layout, nothing to do
        [InlineData(5, 3, true)]  // hardware really does send fewer sliders
        [InlineData(3, 5, true)]  // hardware really does send more sliders
        [InlineData(5, 1, true)]  // the truncated-first-line shape from #125
        [InlineData(5, 0, false)] // empty payload is never a layout change
        [InlineData(5, -1, false)] // defensive: negative counts are rejected
        public void IsRebuildWarranted_OnlyAcceptsAPlausibleNewCount(
            int liveCount, int payloadCount, bool expected)
        {
            Assert.Equal(expected, SliderLayoutReconciler.IsRebuildWarranted(liveCount, payloadCount));
        }

        /// <summary>
        /// Characterization test: the restore loop that shipped before this fix assigned every
        /// overlapping channel unconditionally, so a channel that happened to be unmapped at the
        /// moment of a rebuild overwrote the mapping the profile had just restored. SaveSettings()
        /// then persisted that erasure.
        /// </summary>
        [Fact]
        public void PreviousImplementation_LostMappingsThatThisFixPreserves()
        {
            var restored = FullProfile();
            var previous = Enumerable.Range(0, 5).Select(_ => Unmapped()).ToList();

            var before = OldRestoreLoop(restored, previous, 5);
            var after = SliderLayoutReconciler.ApplyPreviousMappings(restored, previous, 5);

            Assert.All(before, group => Assert.Empty(group)); // every mapping destroyed
            Assert.Equal(Describe(FullProfile()), Describe(after)); // all five survive
        }

        private static List<List<AudioTarget>> OldRestoreLoop(
            List<List<AudioTarget>> restored, List<List<AudioTarget>> current, int count)
        {
            var result = restored.Take(count).ToList();
            for (int i = 0; i < Math.Min(current.Count, count); i++)
            {
                result[i] = current[i];
            }
            return result;
        }

        private static List<List<AudioTarget>> UnmappedList() => new List<List<AudioTarget>>();
    }
}
