namespace DeejNG.Services
{
    // A board that streams continuously can never leave the receive buffer at exactly the
    // same byte count: a count pinned across quiet watchdog ticks means nothing is
    // consuming it, and the port must be cycled.
    public static class SerialStallBreakerPolicy
    {
        // Watchdog ticks are 1 s with a 5 s quiet threshold, so this fires about 8 s into a jam.
        public const int PinnedTickThreshold = 3;

        public static (int PinnedTicks, bool Stalled) Advance(int pinnedTicks, int previousBytes, int bytesToRead)
        {
            if (bytesToRead <= 0 || bytesToRead != previousBytes)
            {
                return (0, false);
            }

            var ticks = pinnedTicks + 1;
            return (ticks, ticks >= PinnedTickThreshold);
        }
    }
}
