namespace DeejNG.Services
{
    public enum WatchdogVerdict
    {
        Healthy,

        // Quiet, but bytes are waiting: the read stalled, not the link.
        DrainPendingBytes,

        Probe,

        Disconnect,
    }

    // A non-empty buffer used to be counted as silence, so the watchdog closed a healthy link
    // whose read had merely stalled. It is proof of life: drain it instead.
    public static class WatchdogPolicy
    {
        public static WatchdogVerdict Decide(bool quiet, int bytesToRead, int noDataCounter, int maxQuietIntervals)
        {
            if (!quiet)
            {
                return WatchdogVerdict.Healthy;
            }

            if (bytesToRead > 0)
            {
                return WatchdogVerdict.DrainPendingBytes;
            }

            return noDataCounter + 1 >= maxQuietIntervals
                ? WatchdogVerdict.Disconnect
                : WatchdogVerdict.Probe;
        }
    }
}
