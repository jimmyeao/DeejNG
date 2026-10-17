using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace DeejNG.Services
{
    public class SerialConnectionManager : IDisposable
    {
        #region Private Fields

        private const bool EnableWatchdog = true;
        private const int MaxLineLength = 200;
        // ---- Tuning ----
        private const int MaxRemainderBytes = 4096;

        // How long the dedicated reader thread dozes between BytesToRead polls. Frames
        // arrive about every 50 ms, so the added latency is a rounding error next to the
        // frame interval.
        private const int ReaderPollMilliseconds = 15;

        private const int WatchdogMaxQuietIntervals = 3;
        private const byte WatchdogProbeByte = 10;
        private static readonly TimeSpan InvalidPortsRetryInterval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan WatchdogQuietThreshold = TimeSpan.FromSeconds(5);
        private int _baudRate = 0;
        private bool[] _buttonStates = Array.Empty<bool>();
        // Track current button states
        private bool _buttonStatesInitialized = false;

        private DateTime _connectionStartTime = DateTime.MinValue;
        // One reset pulse per connection, see CheckConnection.
        private bool _dtrNudged = false;
        private bool _expectingData = false;
        private HashSet<string> _invalidPorts = new HashSet<string>();
        private DateTime _invalidPortsClearTime = DateTime.MinValue;
        private bool _isConnected = false;
        // Protocol validation
        private bool _isProtocolValidated = false;

        private string _lastConnectedPort = string.Empty;
        private DateTime _lastValidDataTimestamp = DateTime.MinValue;
        private string _leftover = string.Empty;
        private bool _manualDisconnect = false;
        private int _noDataCounter = 0;
        private int _numberOfButtons = 0;
        // pending partial line
        // Button handling
        private int _numberOfSliders = 0;

        private volatile int _reading = 0;
        // The dedicated read loop, one thread per connection. See SerialReaderLoop.
        private Thread _readerThread;
        private volatile bool _readerStop;
        private bool _serialDisconnected = false;
        private SerialPort _serialPort;
        private bool _serialPortFullyInitialized = false;
        // Stall-breaker state, driven by the watchdog on quiet ticks. See
        // SerialStallBreakerPolicy.
        private int _stallPinnedTicks = 0;
        private int _stallPreviousBytes = -1;
        private string _userSelectedPort = string.Empty;

        #endregion Private Fields

        #region Public Events

        public event Action<int, bool> ButtonStateChanged;

        // buttonIndex, isPressed
        public event Action Connected;

        public event Action<string> DataReceived;
        public event Action Disconnected;
        public event Action<string> ProtocolValidated;

        #endregion Public Events

        #region Public Properties

        public int CurrentBaudRate => _baudRate > 0 ? _baudRate : 9600;
        public string CurrentPort => _serialPort?.PortName ?? string.Empty;
        public bool IsConnected => _isConnected && !_serialDisconnected;
        public bool IsFullyInitialized => _serialPortFullyInitialized;
        public bool IsProtocolValidated => _isProtocolValidated;
        public string LastConnectedPort => _lastConnectedPort;

        #endregion Public Properties

        #region Public Methods

        public void CheckConnection()
        {
            if (!IsConnected || !EnableWatchdog) return;

            try
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                {

                    HandleSerialDisconnection();
                    return;
                }

                // Check for protocol validation timeout
                if (!_isProtocolValidated && _connectionStartTime != DateTime.MinValue)
                {
                    var elapsed = DateTime.Now - _connectionStartTime;
                    if (elapsed >= ValidationTimeout)
                    {
                        // A sketch that only reports when a slider moves can sit silent on a port
                        // that is perfectly healthy. Asserting DTR drops the Arduino's reset line
                        // and makes it boot and emit a frame, and DtrEnable is settable on an open
                        // port, so this is done without closing and reopening it. Only a board that
                        // stays silent after this is written off as invalid.
                        if (!_dtrNudged)
                        {
                            _dtrNudged = true;
                            try { _serialPort.DtrEnable = true; } catch { }
                            _connectionStartTime = DateTime.Now;
                            return;
                        }

                        // Mark this port as invalid
                        if (!string.IsNullOrEmpty(CurrentPort))
                        {
                            _invalidPorts.Add(CurrentPort);
                            if (_invalidPortsClearTime == DateTime.MinValue)
                            {
                                _invalidPortsClearTime = DateTime.Now;
                            }
                        }

                        HandleSerialDisconnection();
                        return;
                    }
                }

                if (_expectingData)
                {
                    var quiet = DateTime.Now - _lastValidDataTimestamp >= WatchdogQuietThreshold;
                    var bytesToRead = _serialPort.BytesToRead;

                    // The rule itself lives in WatchdogPolicy so that "bytes waiting in the
                    // buffer prove the link is alive" can be pinned down by a test instead of
                    // only being checkable with the hardware attached.
                    switch (WatchdogPolicy.Decide(quiet, bytesToRead, _noDataCounter, WatchdogMaxQuietIntervals))
                    {
                        case WatchdogVerdict.Healthy:
                            _noDataCounter = 0;
                            ResetStallTracking();
                            break;

                        case WatchdogVerdict.DrainPendingBytes:
                            // Read them rather than counting them as silence: the connection is
                            // kept and the stalled read is revived in the same step. See
                            // WatchdogPolicy for what counting them used to cost.
                            DrainSerialBuffer();
                            _noDataCounter = 0;

                            // Normally the drain above consumes the buffer and the next quiet
                            // tick sees a different byte count. When the count is pinned at
                            // the same value instead, the drain was a no-op behind a jammed
                            // read path, and after a few pinned ticks the only remedy left is
                            // to cycle the port. Reopening no longer resets the board, because
                            // DTR is held low at open.
                            if (TrackStallAndCheckBreaker(bytesToRead))
                            {
                                HandleSerialDisconnection();
                            }
                            break;

                        case WatchdogVerdict.Probe:
                            _noDataCounter++;
                            ResetStallTracking();

                            if (WatchdogProbeByte != 0)
                            {
                                try { _serialPort.Write(new[] { WatchdogProbeByte }, 0, 1); }
                                catch { /* ignore; disconnect if persistent */ }
                            }
                            break;

                        case WatchdogVerdict.Disconnect:
                            _noDataCounter++;
                            HandleSerialDisconnection();
                            _noDataCounter = 0;
                            break;
                    }
                }
                else if (IsConnected && (DateTime.Now - _lastValidDataTimestamp).TotalSeconds > 10)
                {
                    _expectingData = true;
                }
            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// Clears the invalid ports list - useful for troubleshooting or when device firmware is updated
        /// </summary>
        public void ClearInvalidPorts()
        {

            _invalidPorts.Clear();
            _invalidPortsClearTime = DateTime.Now;
        }

        /// <summary>
        /// Configures the number of sliders and buttons expected in the serial data.
        /// This must be called before button events will be raised.
        /// </summary>
        /// <param name="sliderCount">Number of slider values expected</param>
        /// <param name="buttonCount">Number of button values expected</param>
        public void ConfigureLayout(int sliderCount, int buttonCount)
        {
            _numberOfSliders = sliderCount;
            _numberOfButtons = buttonCount;

            if (buttonCount > 0)
            {
                _buttonStates = new bool[buttonCount];
                _buttonStatesInitialized = false; // Reset on reconfiguration

            }
            else
            {
                _buttonStates = Array.Empty<bool>();
                _buttonStatesInitialized = false;
            }
        }

        public void Dispose()
        {
            ClosePort();
            _leftover = string.Empty;
        }

        public void HandleSerialDisconnection()
        {
            if (_serialDisconnected) return;


            _serialDisconnected = true;
            _isConnected = false;

            Disconnected?.Invoke();
            ClosePort();
        }

        public void InitSerial(string portName, int baudRate)
        {
            try
            {
                _baudRate = baudRate;
                if (string.IsNullOrWhiteSpace(portName))
                {

                    return;
                }

                // Check if this port was recently marked as invalid
                if (IsPortMarkedInvalid(portName))
                {

                    _isConnected = false;
                    _serialDisconnected = true;
                    return;
                }

                var available = SerialPort.GetPortNames();
                if (Array.IndexOf(available, portName) < 0)
                {

                    _isConnected = false;
                    _serialDisconnected = true;
                    return;
                }

                ClosePort();

                _baudRate = baudRate;
                _serialPort = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    // The default 4096 byte buffer holds only about four seconds of a continuously
                    // streaming sketch, and the reader can be starved for longer than that when the
                    // machine is busy. Once the buffer fills, the driver returns bytes with values
                    // dropped or duplicated mid-number, which is what the UI used to rebuild itself
                    // from. 32 KiB covers roughly half a minute of the same stream.
                    ReadBufferSize = 32768,
                    // Held deasserted so that opening the port leaves a board that is already
                    // running alone. Asserting DTR here resets the Arduino, which then reports
                    // every slider at zero for about half a minute before the readings come
                    // back, one channel at a time - inverted, that is 100% on every channel.
                    // A board that never speaks is nudged instead, see CheckConnection.
                    DtrEnable = false,
                    RtsEnable = true,
                    NewLine = "\n"
                };

                // Don't carry bytes over from a previous connection: a stale partial line would
                // be concatenated onto the first line of this one. Cleared before the reader is
                // started so no data can be framed ahead of it.
                _leftover = string.Empty;

                // The driver's DataReceived event is deliberately not used. .NET raises it on a
                // threadpool thread while holding the port's internal lock, and a read that
                // stalls inside the handler parks every later event behind that lock - the
                // 2026-10-17 thread dump caught eight of them queued behind one, with the
                // buffer full and unread. (When this port still subscribed at
                // ReceivedBytesThreshold = 1, each byte queued another work item, which is the
                // thread-pool churn the old comment warned about.) A reader of our own cannot
                // jam that way; see SerialReaderLoop.
                _serialPort.ErrorReceived += SerialPort_ErrorReceived;
                _serialPort.Open();

                _isConnected = true;
                _serialDisconnected = false;
                _serialPortFullyInitialized = false;
                _lastConnectedPort = portName;

                _lastValidDataTimestamp = DateTime.Now;
                _noDataCounter = 0;
                _expectingData = false;

                // Reset protocol validation state
                _isProtocolValidated = false;
                _connectionStartTime = DateTime.Now;
                _dtrNudged = false;

                // Fresh link, fresh breaker state.
                ResetStallTracking();

                Connected?.Invoke();

                _readerStop = false;
                _readerThread = new Thread(SerialReaderLoop)
                {
                    IsBackground = true,
                    Name = "DeejNG-SerialReader"
                };
                _readerThread.Start();

            }
            catch (Exception ex)
            {

                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;
            }
        }

        public void ManualDisconnect()
        {
            try
            {

                _manualDisconnect = true;

                ClosePort();

                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;

                // Clear invalid ports list on manual disconnect - user may be troubleshooting
                if (_invalidPorts.Count > 0)
                {

                    _invalidPorts.Clear();
                }

                Disconnected?.Invoke();
            }
            catch (Exception ex)
            {

            }
        }

        public void SetUserSelectedPort(string portName)
        {
            _userSelectedPort = portName;
            if (_manualDisconnect) _manualDisconnect = false;

            // Clear this port from invalid list when user manually selects it
            if (_invalidPorts.Contains(portName))
            {
                _invalidPorts.Remove(portName);

            }


        }

        public bool ShouldAttemptReconnect() => _serialDisconnected && !_manualDisconnect;

        public bool TryConnectToSavedPort(string savedPortName, int baudRate)
        {
            if (string.IsNullOrWhiteSpace(savedPortName))
                return false;

            // Optionally update internal field
            _baudRate = baudRate;
            try
            {
                if (IsConnected) return true;

                string portToTry = !string.IsNullOrEmpty(_userSelectedPort) ? _userSelectedPort : savedPortName;
                if (string.IsNullOrWhiteSpace(portToTry))
                {

                    return false;
                }

                var available = SerialPort.GetPortNames();
                if (Array.IndexOf(available, portToTry) < 0)
                {

                    return false;
                }

                // Clear saved port from invalid list before attempting auto-connect
                // (it may have been marked invalid in a previous session or failed attempt)
                if (_invalidPorts.Contains(portToTry))
                {
                    _invalidPorts.Remove(portToTry);

                }

                // Reuse last configured baud rate; default to 9600 if unknown.
                InitSerial(portToTry, _baudRate > 0 ? _baudRate : 9600);

                if (IsConnected) _userSelectedPort = string.Empty;
                return IsConnected;
            }
            catch (Exception ex)
            {

                return false;
            }
        }

        #endregion Public Methods

        #region Private Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string FilterPrintable(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if ((ch >= 0x20 && ch <= 0x7E) || ch == '\r' || ch == '\n')
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private void ClosePort()
        {
            try
            {
                // Stop the reader before the port is torn down, so it is not mid-read on a
                // port being disposed. Bounded join: if a drain is wedged, this port is
                // being cycled anyway and the background thread dies with the process.
                _readerStop = true;
                var reader = _readerThread;
                if (reader != null && reader.IsAlive)
                {
                    try { reader.Join(500); } catch { }
                }
                _readerThread = null;

                if (_serialPort != null)
                {
                    _serialPort.ErrorReceived -= SerialPort_ErrorReceived;

                    if (_serialPort.IsOpen)
                    {
                        try { _serialPort.DiscardInBuffer(); } catch { }
                        try { _serialPort.DiscardOutBuffer(); } catch { }
                        _serialPort.Close();
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                }

                // Reset button state initialization flag on disconnect
                _buttonStatesInitialized = false;

                // Drop any partial line so it cannot be concatenated onto the next connection's
                // first line.
                _leftover = string.Empty;
            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// Checks if a port is marked as invalid (failed protocol validation)
        /// </summary>
        private bool IsPortMarkedInvalid(string portName)
        {
            // Clear invalid ports list after retry interval
            if (DateTime.Now - _invalidPortsClearTime > InvalidPortsRetryInterval)
            {

                _invalidPorts.Clear();
                _invalidPortsClearTime = DateTime.Now;
            }

            return _invalidPorts.Contains(portName);
        }

        /// <summary>
        /// Processes a complete serial line, auto-detecting and separating slider and button data.
        /// Sliders: 0-1023 (or 9999 for inline mute)
        /// Buttons: 10000 (OFF) or 10001 (ON)
        /// </summary>
        private void ProcessSerialLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // Framing is checked on every line, not only until the protocol is first recognised.
            // The link runs at the full rate the sketch can drive it, so a starved reader lets the
            // driver buffer overrun and the bytes that come back have values dropped, duplicated or
            // reordered mid-number. Those lines used to be accepted unconditionally once the
            // protocol had been seen once, and every one whose field count differed from the live
            // layout rebuilt the whole slider panel: a burst of them became a rebuild storm that
            // saturated the UI thread for tens of seconds.
            if (!SerialFrameValidator.IsPlausible(line))
            {
                // Don't process invalid data
                return;
            }

            // The first line that looks like deej data proves this port is talking the protocol.
            if (!_isProtocolValidated)
            {
                _isProtocolValidated = true;

                ProtocolValidated?.Invoke(CurrentPort);
            }

            // Split the line
            string[] parts = line.Split('|');
            if (parts.Length == 0) return;

            // Auto-detect sliders vs buttons by value range
            var sliderParts = new List<string>();
            var buttonValues = new List<float>();

            for (int i = 0; i < parts.Length; i++)
            {
                if (float.TryParse(parts[i].Trim(), out float value))
                {
                    // Button values are 10000 or 10001
                    if (value >= 9999.5f)
                    {
                        buttonValues.Add(value);
                    }
                    else
                    {
                        // Slider value (0-1023 range or 9999 inline mute)
                        sliderParts.Add(parts[i]);
                    }
                }
            }

            // Raise slider data event if we have any sliders
            if (sliderParts.Count > 0)
            {
                string sliderData = string.Join("|", sliderParts);
                DataReceived?.Invoke(sliderData);
            }

            // Process buttons if we have any
            if (buttonValues.Count > 0)
            {
                // Resize button state array if needed
                if (_buttonStates.Length != buttonValues.Count)
                {
                    _buttonStates = new bool[buttonValues.Count];
                    _buttonStatesInitialized = false; // Need to initialize states from hardware

                }

                // Check each button for state changes
                for (int i = 0; i < buttonValues.Count; i++)
                {
                    // 10001 = pressed, 10000 = not pressed
                    bool isPressed = buttonValues[i] >= 10000.5f;

                    // Check for state change
                    if (_buttonStates[i] != isPressed)
                    {
                        _buttonStates[i] = isPressed;

                        // BUGFIX: Don't fire events on first sync after resize to prevent spurious actions
                        // This prevents play/pause from triggering on app startup or port change
                        if (_buttonStatesInitialized)
                        {
                            // Raise event for both press and release (for UI indicator updates)
                            // Note: MainWindow.HandleButtonPress only executes actions on press

                            ButtonStateChanged?.Invoke(i, isPressed);
                        }

                    }
                }

                // Mark as initialized after first data packet
                if (!_buttonStatesInitialized)
                {
                    _buttonStatesInitialized = true;

                }
            }
        }

        /// <summary>
        /// The dedicated read loop: one background thread per connection, polling
        /// BytesToRead and draining whenever bytes are waiting. BytesToRead is a
        /// ClearCommError call that takes no lock a reader can be parked behind, so this
        /// loop keeps draining even when the machine is under load - which is what the
        /// driver's DataReceived event could not guarantee, raised as it is on a starved
        /// threadpool thread from inside the port's internal lock. A thread of our own
        /// also sits outside the threadpool, so pool starvation cannot queue behind it.
        /// Exits when the connection is closed; the next one starts its own reader.
        /// </summary>
        private void SerialReaderLoop()
        {
            while (!_readerStop)
            {
                try
                {
                    var port = _serialPort;
                    if (port == null || !port.IsOpen) return;

                    if (port.BytesToRead > 0)
                        DrainSerialBuffer();
                    else
                        Thread.Sleep(ReaderPollMilliseconds);
                }
                catch
                {
                    // The port was torn down underneath us by a reconnect or a close.
                    return;
                }
            }
        }

        private void ResetStallTracking()
        {
            _stallPinnedTicks = 0;
            _stallPreviousBytes = -1;
        }

        /// <summary>
        /// Folds one quiet watchdog tick into the stall breaker. Returns true when the
        /// receive byte count has been pinned long enough that the port must be cycled;
        /// see SerialStallBreakerPolicy.
        /// </summary>
        private bool TrackStallAndCheckBreaker(int bytesToRead)
        {
            var (pinnedTicks, stalled) = SerialStallBreakerPolicy.Advance(
                _stallPinnedTicks, _stallPreviousBytes, bytesToRead);

            _stallPinnedTicks = pinnedTicks;
            _stallPreviousBytes = bytesToRead;

            if (stalled)
            {
                ResetStallTracking();
            }

            return stalled;
        }

        /// <summary>
        /// Reads whatever the driver has buffered and frames the complete lines in it.
        /// Runs from the dedicated reader thread, and from the watchdog when bytes are
        /// still waiting but the reader has stopped delivering - so a stalled read is
        /// revived without closing the port, and without disturbing the board on the
        /// other end.
        /// </summary>
        private void DrainSerialBuffer()
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            // Prevent re-entrant entry from the driver
            if (Interlocked.Exchange(ref _reading, 1) == 1)
                return;

            try
            {
                var incoming = _serialPort.ReadExisting();

                _lastValidDataTimestamp = DateTime.Now;
                _expectingData = true;
                _noDataCounter = 0;

                if (_leftover.Length > MaxRemainderBytes) _leftover = string.Empty;

                incoming = FilterPrintable(incoming);
                if (incoming.Length == 0) return;

                var combined = _leftover + incoming;

                int start = 0;
                for (int i = 0; i < combined.Length; i++)
                {
                    char c = combined[i];
                    if (c == '\n' || c == '\r')
                    {
                        int len = i - start;
                        if (len > 0)
                        {
                            var line = combined.AsSpan(start, len).Trim().ToString();
                            if (line.Length > 0 && line.Length <= MaxLineLength)
                            {
                                ProcessSerialLine(line);

                                if (!_serialPortFullyInitialized)
                                {
                                    _serialPortFullyInitialized = true;

                                }
                            }
                        }

                        // swallow paired CRLF
                        if (i + 1 < combined.Length &&
                            ((c == '\r' && combined[i + 1] == '\n') ||
                             (c == '\n' && combined[i + 1] == '\r')))
                        {
                            i++;
                        }
                        start = i + 1;
                    }
                }

                _leftover = (start < combined.Length) ? combined[start..] : string.Empty;
            }
            catch (IOException) { HandleSerialDisconnection(); }
            catch (InvalidOperationException) { HandleSerialDisconnection(); }
            catch (Exception ex)
            {

                _leftover = string.Empty;
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {

            if (e.EventType == SerialError.Frame || e.EventType == SerialError.RXOver ||
                e.EventType == SerialError.Overrun || e.EventType == SerialError.RXParity)
            {
                HandleSerialDisconnection();
            }
        }

        #endregion Private Methods
    }
}
