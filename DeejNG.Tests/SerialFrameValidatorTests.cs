using DeejNG.Services;
using System.Globalization;
using System.Linq;
using Xunit;

namespace DeejNG.Tests
{
    // The damaged shapes below were recorded during the 2026-10-17 rebuild storm. Damage that
    // only changes the field count is SliderCountStabilizer's job, not this check's.
    public class SerialFrameValidatorTests
    {
        [Theory]
        [InlineData("34|745|244|227|432")] // a healthy five-slider frame
        [InlineData("34|745|244|227|432|10000|10000|10000|10000|10000")] // sliders plus five buttons
        [InlineData("0.5|0.3|0.8|1.0|0.0")] // normalised firmware
        [InlineData("34|745|9999|227|432")] // inline mute marker
        [InlineData("327")] // the truncated first line after the DTR reset
        [InlineData("10000")] // a lone button state
        public void HealthyLines_AreAccepted(string line)
        {
            Assert.True(SerialFrameValidator.IsPlausible(line));
        }

        [Theory]
        [InlineData("34|7455555555555555|244|227|432|10000|10000|10000|10000|10000")] // digits duplicated
        [InlineData("34|745|244|227|432|10000|10000|10000|1000010000|10000|10000")] // join inside a button value
        [InlineData("34|745|244|227|40000|10000|10000|10000")] // value in the gap between ADC and button
        [InlineData("34|745|-9999|227|432")] // far outside every accepted range
        [InlineData("3000")] // a lone value too large for an ADC and too small for a button
        [InlineData("1e5")] // exponent notation is not what the firmware prints
        [InlineData("34|745|")] // trailing separator leaves an empty field
        [InlineData("34|745||227")] // empty field in the middle
        [InlineData("abc|745|244")] // not numeric
        [InlineData("34|745|nan|227")] // unparsable as a plain value
        [InlineData("")] // nothing at all
        [InlineData("   ")] // whitespace only
        public void DamagedLines_AreRejected(string line)
        {
            Assert.False(SerialFrameValidator.IsPlausible(line));
        }

        [Fact]
        public void OneDamagedFieldRejectsTheWholeLine()
        {
            // Only the middle field is damaged, but a frame is only usable as a whole.
            Assert.False(SerialFrameValidator.IsPlausible("34|745|766612|227|432"));
        }

        [Fact]
        public void ALineWithMoreFieldsThanAnyRealLayout_IsRejected()
        {
            var line = string.Join("|", Enumerable.Repeat("512", SerialFrameValidator.MaxFieldCount + 1));

            Assert.False(SerialFrameValidator.IsPlausible(line));
        }

        [Fact]
        public void ALineAtTheFieldCountLimit_IsStillAccepted()
        {
            var line = string.Join("|", Enumerable.Repeat("512", SerialFrameValidator.MaxFieldCount));

            Assert.True(SerialFrameValidator.IsPlausible(line));
        }

        [Fact]
        public void FieldsAreTrimmedBeforeBeingJudged()
        {
            Assert.True(SerialFrameValidator.IsPlausible(" 34 | 745 | 244 "));
        }

        [Fact]
        public void DecimalFieldsAreReadInvariantlyWhateverTheSystemLocale()
        {
            // The firmware prints '.' whatever the locale; id-ID uses ',' as its separator.
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("id-ID");

                Assert.True(SerialFrameValidator.IsPlausible("0.5|0.3|0.8|1.0|0.0"));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
