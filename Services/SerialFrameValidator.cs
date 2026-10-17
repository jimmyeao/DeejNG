using System.Globalization;

namespace DeejNG.Services
{
    /// <summary>
    /// Decides whether one line of serial data is plausibly a deej frame.
    ///
    /// A continuously streaming sketch fills the 9600 baud link outright (about 19 frames a
    /// second, all day). When the reader is starved — which happens under heavy system load —
    /// the driver's receive buffer overruns, and the bytes that come back have been dropped,
    /// duplicated or reordered in the middle of a value: "745" arrives as "7455555555555555",
    /// "10000" as "000".
    ///
    /// Validation used to run only until the protocol had been recognised once, after which
    /// every line was accepted as-is. A damaged frame whose field count differed from the live
    /// layout then rebuilt the entire slider panel, so a burst of them became a rebuild storm
    /// that saturated the UI thread for tens of seconds. Checking every line keeps that damage
    /// out of the UI.
    ///
    /// Damage that leaves every value in range is not caught here; that is what
    /// <see cref="SliderCountStabilizer"/> is for.
    /// </summary>
    public static class SerialFrameValidator
    {
        /// <summary>
        /// Longest field the protocol can produce. "10001" is five characters and a normalised
        /// value such as "0.9999" is six, so a longer field is damaged data.
        /// </summary>
        public const int MaxFieldLength = 8;

        /// <summary>
        /// Upper bound on fields in one line. A real layout is sliders plus buttons and comes to
        /// a few dozen at most; a larger count means the line was assembled from damaged bytes.
        /// </summary>
        public const int MaxFieldCount = 32;

        /// <summary>
        /// True when every field parses as a number in one of the ranges the protocol uses.
        /// </summary>
        public static bool IsPlausible(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length == 0 || parts.Length > MaxFieldCount)
                return false;

            for (int i = 0; i < parts.Length; i++)
            {
                if (!IsPlausibleField(parts[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// True when a single field is a number the protocol can legitimately carry.
        ///
        /// The ranges are the ones the serial layer has always accepted for the first three
        /// fields — raw ADC with tolerance, normalised float, the inline mute marker and the two
        /// button states — now applied to every field rather than just the leading ones.
        /// </summary>
        public static bool IsPlausibleField(string field)
        {
            if (string.IsNullOrWhiteSpace(field))
                return false;

            var text = field.Trim();
            if (text.Length > MaxFieldLength)
                return false;

            // The protocol is machine-generated ASCII, so its numbers are invariant whatever
            // locale the app happens to be running under: firmware prints floats with '.', never
            // with the system decimal separator.
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                return false;

            // Values that overflow parse to infinity rather than failing on .NET Core and later.
            if (float.IsNaN(value) || float.IsInfinity(value))
                return false;

            bool isRawAdc = value >= -10f && value <= 1100f;
            bool isNormalised = value >= -0.1f && value <= 1.1f;
            bool isInlineMute = value >= 9998f && value <= 10000f;
            bool isButton = value >= 9999.5f && value <= 10001.5f;

            return isRawAdc || isNormalised || isInlineMute || isButton;
        }
    }
}
