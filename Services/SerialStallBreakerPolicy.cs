namespace DeejNG.Services
{
    /// <summary>
    /// The circuit breaker that cycles the port when the read path is jammed.
    ///
    /// A deej board streams continuously, so on a quiet watchdog tick the receive buffer
    /// should be shrinking or refilling - never sitting at exactly the same byte count.
    /// The 2026-10-17 thread dump showed why it can sit still anyway: the drain thread
    /// parked inside SerialPort.ReadExisting while holding SerialPort's internal lock,
    /// so every later drain - including the watchdog's - was a no-op behind the guard,
    /// and the count stayed pinned (the log recorded 32706 for minutes). When the count
    /// is pinned at one non-zero value for PinnedTickThreshold consecutive quiet ticks
    /// even though the watchdog drained in between, nothing is consuming the buffer, and
    /// the only remedy left is to close and reopen the port - which no longer resets the
    /// board, because DTR is held low at open.
    /// </summary>
    public static class SerialStallBreakerPolicy
    {
        /// <summary>
        /// Consecutive quiet sightings of a pinned byte count after which the link is
        /// cycled. The watchdog ticks every second with a 5 s quiet threshold, so the
        /// third pinned tick breaks the link roughly 8 s into a stall.
        /// </summary>
        public const int PinnedTickThreshold = 3;

        /// <summary>
        /// Folds one quiet watchdog tick into the stall tracker.
        /// </summary>
        /// <param name="pinnedTicks">Consecutive pinned sightings counted so far.</param>
        /// <param name="previousBytes">The byte count seen on the previous quiet tick, or -1 for none.</param>
        /// <param name="bytesToRead">The byte count seen on this tick.</param>
        /// <returns>
        /// The updated pinned-tick count, and whether the link must be cycled. A tick
        /// whose byte count differs from the previous one - or is empty - means the
        /// buffer is moving and resets the tracker.
        /// </returns>
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
