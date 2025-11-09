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
        private SerialPort _serialPort;
        private bool _isConnected = false;
        private bool _serialDisconnected = false;
        private bool _manualDisconnect = false;
        private bool _serialPortFullyInitialized = false;
        private string _lastConnectedPort = string.Empty;
        private string _userSelectedPort = string.Empty;

        private DateTime _lastValidDataTimestamp = DateTime.MinValue;
        private int _noDataCounter = 0;
        private bool _expectingData = false;

        // Protocol validation
        private bool _isProtocolValidated = false;
        private DateTime _connectionStartTime = DateTime.MinValue;
        private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(5);
        private HashSet<string> _invalidPorts = new HashSet<string>();
        private DateTime _invalidPortsClearTime = DateTime.MinValue;
        private static readonly TimeSpan InvalidPortsRetryInterval = TimeSpan.FromMinutes(2);

        private volatile int _reading = 0;      // re-entrancy guard
        private string _leftover = string.Empty; // pending partial line
        private int _baudRate = 0;

        // Button handling
        private int _numberOfSliders = 0;
        private int _numberOfButtons = 0;
        private bool[] _buttonStates = Array.Empty<bool>(); // Track current button states

        // ---- Tuning ----
        private const int MaxRemainderBytes = 4096;
        private const int MaxLineLength = 200;
        private const bool EnableWatchdog = true;
        private static readonly TimeSpan WatchdogQuietThreshold = TimeSpan.FromSeconds(5);
        private const int WatchdogMaxQuietIntervals = 3;
        private const byte WatchdogProbeByte = 10; // 0 to disable probe

        public event Action<string> DataReceived;
        public event Action<int, bool> ButtonStateChanged; // buttonIndex, isPressed
        public event Action Connected;
        public event Action Disconnected;
        public event Action<string> ProtocolValidated; // Raised when valid DeejNG data is received

        public bool IsConnected => _isConnected && !_serialDisconnected;
        public bool IsFullyInitialized => _serialPortFullyInitialized;
        public bool IsProtocolValidated => _isProtocolValidated;
        public string LastConnectedPort => _lastConnectedPort;
        public string CurrentPort => _serialPort?.PortName ?? string.Empty;

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
#if DEBUG
                Debug.WriteLine($"[Serial] Configured for {sliderCount} sliders and {buttonCount} buttons");
#endif
            }
            else
            {
                _buttonStates = Array.Empty<bool>();
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
#if DEBUG
                if (_invalidPorts.Count > 0)
                    Debug.WriteLine($"[Validation] Clearing {_invalidPorts.Count} invalid port(s) - retry interval elapsed");
#endif
                _invalidPorts.Clear();
                _invalidPortsClearTime = DateTime.Now;
            }

            return _invalidPorts.Contains(portName);
        }

        /// <summary>
        /// Validates if the received data is valid DeejNG protocol (pipe-delimited numeric values)
        /// Accepts both raw ADC values (0-1023) and normalized floats (0.0-1.0)
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
                    // Accept two formats:
                    // 1. Raw ADC values (0-1023 for 10-bit ADC, typical Arduino)
                    // 2. Normalized float values (0.0-1.0)
                    // Also allow some tolerance for noise/calibration
                    bool isRawADC = value >= -10 && value <= 1100;  // 0-1023 range with tolerance
                    bool isNormalized = value >= -0.1f && value <= 1.1f;  // 0.0-1.0 range with tolerance

                    if (isRawADC || isNormalized)
                    {
                        validCount++;
                    }
                }
            }

            // Consider valid if at least half of checked values are valid numbers in range
            return validCount >= (maxCheck / 2.0);
        }

        public void InitSerial(string portName, int baudRate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portName))
                {
#if DEBUG
                    Debug.WriteLine("[Serial] Invalid port name provided");
#endif
                    return;
                }

                // Check if this port was recently marked as invalid
                if (IsPortMarkedInvalid(portName))
                {
#if DEBUG
                    Debug.WriteLine($"[Validation] Skipping port {portName} - marked as invalid (no valid DeejNG data)");
#endif
                    _isConnected = false;
                    _serialDisconnected = true;
                    return;
                }

                var available = SerialPort.GetPortNames();
                if (Array.IndexOf(available, portName) < 0)
                {
#if DEBUG
                    Debug.WriteLine($"[Serial] Port {portName} not available: [{string.Join(", ", available)}]");
#endif
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
                    // Slider messages are tiny; keep threshold low so the handler fires promptly.
                    ReceivedBytesThreshold = 1,
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
#if DEBUG
                Debug.WriteLine($"[Serial] Connected to {portName} @ {baudRate} - awaiting protocol validation");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[Serial] Failed to open port {portName}: {ex.Message}");
#endif
                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;
            }
        }

        public void ManualDisconnect()
        {
            try
            {
#if DEBUG
                Debug.WriteLine("[Manual] User initiated manual disconnect");
#endif
                _manualDisconnect = true;

                ClosePort();

                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;

                // Clear invalid ports list on manual disconnect - user may be troubleshooting
                if (_invalidPorts.Count > 0)
                {
#if DEBUG
                    Debug.WriteLine($"[Manual] Clearing {_invalidPorts.Count} invalid port(s) on manual disconnect");
#endif
                    _invalidPorts.Clear();
                }

                Disconnected?.Invoke();
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to disconnect manually: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// Clears the invalid ports list - useful for troubleshooting or when device firmware is updated
        /// </summary>
        public void ClearInvalidPorts()
        {
#if DEBUG
            if (_invalidPorts.Count > 0)
                Debug.WriteLine($"[Validation] Manually clearing {_invalidPorts.Count} invalid port(s)");
#endif
            _invalidPorts.Clear();
            _invalidPortsClearTime = DateTime.Now;
        }

        public bool TryConnectToSavedPort(string savedPortName)
        {
            try
            {
                if (IsConnected) return true;

                string portToTry = !string.IsNullOrEmpty(_userSelectedPort) ? _userSelectedPort : savedPortName;
                if (string.IsNullOrWhiteSpace(portToTry))
                {
#if DEBUG
                    Debug.WriteLine("[AutoConnect] No saved or user-selected port");
#endif
                    return false;
                }

                var available = SerialPort.GetPortNames();
                if (Array.IndexOf(available, portToTry) < 0)
                {
#if DEBUG
                    Debug.WriteLine($"[AutoConnect] Port '{portToTry}' not available. Available: [{string.Join(", ", available)}]");
#endif
                    return false;
                }

                // Clear saved port from invalid list before attempting auto-connect
                // (it may have been marked invalid in a previous session or failed attempt)
                if (_invalidPorts.Contains(portToTry))
                {
                    _invalidPorts.Remove(portToTry);
#if DEBUG
                    Debug.WriteLine($"[AutoConnect] Removed {portToTry} from invalid list for auto-connect attempt");
#endif
                }

                // Reuse last configured baud rate; default to 9600 if unknown.
                InitSerial(portToTry, _baudRate > 0 ? _baudRate : 9600);

                if (IsConnected) _userSelectedPort = string.Empty;
                return IsConnected;
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[AutoConnect] Exception: {ex.Message}");
#endif
                return false;
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
#if DEBUG
                Debug.WriteLine($"[UI] User selected port {portName} - removed from invalid list");
#endif
            }

#if DEBUG
            Debug.WriteLine($"[UI] User selected port: {portName}");
#endif
        }

        public void CheckConnection()
        {
            if (!IsConnected || !EnableWatchdog) return;

            try
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                {
#if DEBUG
                    Debug.WriteLine("[SerialWatchdog] Serial port closed unexpectedly");
#endif
                    HandleSerialDisconnection();
                    return;
                }

                // Check for protocol validation timeout
                if (!_isProtocolValidated && _connectionStartTime != DateTime.MinValue)
                {
                    var elapsed = DateTime.Now - _connectionStartTime;
                    if (elapsed >= ValidationTimeout)
                    {
#if DEBUG
                        Debug.WriteLine($"[Validation] Protocol validation timeout ({elapsed.TotalSeconds:F1}s) - no valid DeejNG data received from {CurrentPort}");
#endif
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
#if DEBUG
                        Debug.WriteLine($"[SerialWatchdog] Quiet for {elapsed.TotalSeconds:F1}s (#{_noDataCounter})");
#endif
                        if (_serialPort.BytesToRead == 0 && WatchdogProbeByte != 0)
                        {
                            try { _serialPort.Write(new[] { WatchdogProbeByte }, 0, 1); }
                            catch { /* ignore; disconnect if persistent */ }
                        }

                        if (_noDataCounter >= WatchdogMaxQuietIntervals)
                        {
#if DEBUG
                            Debug.WriteLine("[SerialWatchdog] Too many quiet intervals, considering disconnected");
#endif
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
#if DEBUG
                Debug.WriteLine($"[SerialWatchdog] Error: {ex.Message}");
#endif
            }
        }

        public bool ShouldAttemptReconnect() => _serialDisconnected && !_manualDisconnect;

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
#if DEBUG
                                    Debug.WriteLine("[Serial] Port fully initialized and receiving data");
#endif
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
#if DEBUG
                Debug.WriteLine($"[ERROR] Serial read: {ex.Message}");
#endif
                _leftover = string.Empty;
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
#if DEBUG
            Debug.WriteLine($"[Serial] Error received: {e.EventType}");
#endif
            if (e.EventType == SerialError.Frame || e.EventType == SerialError.RXOver ||
                e.EventType == SerialError.Overrun || e.EventType == SerialError.RXParity)
            {
                HandleSerialDisconnection();
            }
        }

        public void HandleSerialDisconnection()
        {
            if (_serialDisconnected) return;

#if DEBUG
            Debug.WriteLine("[Serial] Disconnection detected");
#endif
            _serialDisconnected = true;
            _isConnected = false;

            Disconnected?.Invoke();
            ClosePort();
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
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to cleanup serial port: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// Processes a complete serial line, separating slider and button data.
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
#if DEBUG
                    Debug.WriteLine($"[Validation] Protocol validated for port {CurrentPort}");
#endif
                    ProtocolValidated?.Invoke(CurrentPort);
                }
                else
                {
#if DEBUG
                    Debug.WriteLine($"[Validation] Invalid data received: {line}");
#endif
                    // Don't process invalid data
                    return;
                }
            }

            // If no buttons configured, pass entire line to DataReceived
            if (_numberOfButtons == 0 || _numberOfSliders == 0)
            {
                DataReceived?.Invoke(line);
                return;
            }

            // Split the line
            string[] parts = line.Split('|');

            // Separate slider data from button data
            int expectedTotal = _numberOfSliders + _numberOfButtons;

            // If we get fewer values than expected, treat all as sliders (backward compatible)
            if (parts.Length <= _numberOfSliders)
            {
                DataReceived?.Invoke(line);
                return;
            }

            // Extract slider portion
            var sliderParts = new string[_numberOfSliders];
            Array.Copy(parts, 0, sliderParts, 0, Math.Min(_numberOfSliders, parts.Length));
            string sliderData = string.Join("|", sliderParts);

            // Raise slider data event
            DataReceived?.Invoke(sliderData);

            // Process button data (starts after slider values)
            int buttonStartIndex = _numberOfSliders;
            int buttonCount = Math.Min(_numberOfButtons, parts.Length - buttonStartIndex);

            for (int i = 0; i < buttonCount; i++)
            {
                if (int.TryParse(parts[buttonStartIndex + i].Trim(), out int buttonValue))
                {
                    bool isPressed = buttonValue == 1;

                    // Check for state change
                    if (i < _buttonStates.Length && _buttonStates[i] != isPressed)
                    {
                        _buttonStates[i] = isPressed;

                        // Only raise event on button press (not release) to avoid double-triggering
                        if (isPressed)
                        {
#if DEBUG
                            Debug.WriteLine($"[Serial] Button {i} pressed");
#endif
                            ButtonStateChanged?.Invoke(i, isPressed);
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            ClosePort();
            _leftover = string.Empty;
        }
    }
}
