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
        private bool _serialDisconnected = false;
        private SerialPort _serialPort;
        private bool _serialPortFullyInitialized = false;
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
                    var elapsed = DateTime.Now - _lastValidDataTimestamp;

                    if (elapsed >= WatchdogQuietThreshold)
                    {
                        _noDataCounter++;

                        if (_serialPort.BytesToRead == 0 && WatchdogProbeByte != 0)
                        {
                            try { _serialPort.Write(new[] { WatchdogProbeByte }, 0, 1); }
                            catch { /* ignore; disconnect if persistent */ }
                        }

                        if (_noDataCounter >= WatchdogMaxQuietIntervals)
                        {

                            HandleSerialDisconnection();
                            _noDataCounter = 0;
                        }
                    }
                    else
                    {
                        _noDataCounter = 0;
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
                    // Typical slider message is ~20-30 bytes (e.g., "0.5|0.3|0.8|1.0|0.0\n")
                    // Setting threshold to 8 reduces DataReceived events while maintaining responsiveness.
                    // IMPORTANT: Threshold=1 was causing massive thread pool churn (QueueUserWorkItemCallback leak)
                    // because each byte triggered a new thread pool work item.
                    ReceivedBytesThreshold = 8,
                    DtrEnable = true,
                    RtsEnable = true,
                    NewLine = "\n"
                };

                _serialPort.DataReceived += SerialPort_DataReceived;
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

                Connected?.Invoke();

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
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;
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
        /// Validates if the received data is valid DeejNG protocol (pipe-delimited numeric values)
        /// Accepts:
        /// - Raw ADC values (0-1023)
        /// - Normalized floats (0.0-1.0)
        /// - Inline mute trigger (9999)
        /// - Button states (10000=OFF, 10001=ON)
        /// </summary>
        private bool IsValidDeejNGData(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');

            // DeejNG data should have at least one value
            if (parts.Length == 0)
                return false;

            // Check if at least the first few values are valid numbers
            int validCount = 0;
            int maxCheck = Math.Min(parts.Length, 3); // Check first 3 values

            for (int i = 0; i < maxCheck; i++)
            {
                if (float.TryParse(parts[i].Trim(), out float value))
                {
                    // Accept four formats:
                    // 1. Raw ADC values (0-1023 for 10-bit ADC, typical Arduino)
                    // 2. Normalized float values (0.0-1.0)
                    // 3. Inline mute trigger value (9999)
                    // 4. Button states (10000=OFF, 10001=ON)
                    // Also allow some tolerance for noise/calibration
                    bool isRawADC = value >= -10 && value <= 1100;  // 0-1023 range with tolerance
                    bool isNormalized = value >= -0.1f && value <= 1.1f;  // 0.0-1.0 range with tolerance
                    bool isInlineMute = value >= 9998 && value <= 10000;  // 9999 inline mute trigger with tolerance
                    bool isButton = value >= 9999.5f && value <= 10001.5f;  // 10000 or 10001 button states

                    if (isRawADC || isNormalized || isInlineMute || isButton)
                    {
                        validCount++;
                    }
                }
            }

            // Consider valid if at least half of checked values are valid numbers in range
            return validCount >= (maxCheck / 2.0);
        }
        /// <summary>
        /// Processes a complete serial line, auto-detecting and separating slider and button data.
        /// Sliders: 0-1023 (or 9999 for inline mute)
        /// Buttons: 10000 (OFF) or 10001 (ON)
        /// </summary>
        private void ProcessSerialLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // Validate protocol if not yet validated
            if (!_isProtocolValidated)
            {
                if (IsValidDeejNGData(line))
                {
                    _isProtocolValidated = true;

                    ProtocolValidated?.Invoke(CurrentPort);
                }
                else
                {

                    // Don't process invalid data
                    return;
                }
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

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                HandleSerialDisconnection();
                return;
            }

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
