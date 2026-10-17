namespace DeejNG.Services
{
    /// <summary>
    /// What the watchdog should do on one tick of SerialConnectionManager.CheckConnection.
    /// </summary>
    public enum WatchdogVerdict
    {
        /// <summary>Data arrived within the quiet threshold, so the link is demonstrably live.</summary>
        Healthy,

        /// <summary>
        /// The read has gone quiet, but bytes are waiting in the driver's buffer: the link is
        /// alive and our reading of it has stalled. Drain instead of counting this as silence.
        /// </summary>
        DrainPendingBytes,

        /// <summary>Quiet with an empty buffer, but not for long enough to give up yet.</summary>
        Probe,

        /// <summary>Quiet and empty past the limit, so the link is treated as gone.</summary>
        Disconnect,
    }

    /// <summary>
    /// Decides what the watchdog does on a single tick.
    ///
    /// The watchdog used to consult the receive buffer only to decide whether to send a probe
    /// byte, never as evidence of liveness, so it counted silence while thousands of bytes sat
    /// unread and then disconnected a link that was perfectly healthy. Closing the port drops
    /// the stream, and reopening it used to assert DTR, which resets the Arduino - it then
    /// reports every slider at zero for about half a minute before the readings come back, one
    /// channel at a time, and with inverted sliders that is 100% on every channel.
    ///
    /// A non-empty buffer is therefore treated as proof of life: the bytes are read rather than
    /// counted towards a disconnect, which both saves the connection and revives the read.
    /// </summary>
    public static class WatchdogPolicy
    {
        /// <summary>
        /// Decides the verdict for one tick.
        /// </summary>
        /// <param name="quiet">True when no valid data has arrived within the quiet threshold.</param>
        /// <param name="bytesToRead">Bytes waiting in the driver's receive buffer.</param>
        /// <param name="noDataCounter">Quiet intervals observed so far, before this tick.</param>
        /// <param name="maxQuietIntervals">Intervals of empty-buffer silence that force a disconnect.</param>
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
