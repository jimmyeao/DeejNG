using System.Globalization;

namespace DeejNG.Services
{
    // Overrun on the saturated 9600 baud link returns bytes with values dropped or duplicated
    // mid-number ("745" as "7455555..."). Validation used to stop after the first good frame,
    // so each damaged one could rebuild the whole slider panel; this gate runs on every line.
    public static class SerialFrameValidator
    {
        // "10001" is five characters, so a longer field is damaged data.
        public const int MaxFieldLength = 8;

        // A real layout is sliders plus buttons, a few dozen at most.
        public const int MaxFieldCount = 32;

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

        public static bool IsPlausibleField(string field)
        {
            if (string.IsNullOrWhiteSpace(field))
                return false;

            var text = field.Trim();
            if (text.Length > MaxFieldLength)
                return false;

            // The firmware prints '.' whatever locale the machine runs under.
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
