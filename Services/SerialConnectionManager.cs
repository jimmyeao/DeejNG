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

        private volatile int _reading = 0;      // re-entrancy guard
        private string _leftover = string.Empty; // pending partial line
        private int _baudRate = 0;

        // ---- Tuning ----
        private const int MaxRemainderBytes = 4096;
        private const int MaxLineLength = 200;
        private const bool EnableWatchdog = true;
        private static readonly TimeSpan WatchdogQuietThreshold = TimeSpan.FromSeconds(5);
        private const int WatchdogMaxQuietIntervals = 3;
        private const byte WatchdogProbeByte = 10; // 0 to disable probe

        public event Action<string> DataReceived;
        public event Action Connected;
        public event Action Disconnected;

        public bool IsConnected => _isConnected && !_serialDisconnected;
        public bool IsFullyInitialized => _serialPortFullyInitialized;
        public string LastConnectedPort => _lastConnectedPort;
        public string CurrentPort => _serialPort?.PortName ?? string.Empty;

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

                Connected?.Invoke();
#if DEBUG
                Debug.WriteLine($"[Serial] Connected to {portName} @ {baudRate}");
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

                Disconnected?.Invoke();
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to disconnect manually: {ex.Message}");
#endif
            }
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
                                DataReceived?.Invoke(line);

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

        public void Dispose()
        {
            ClosePort();
            _leftover = string.Empty;
        }
    }
}
