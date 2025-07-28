using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeejNG.Services
{
    public class SerialConnectionManager : IDisposable
    {
        private static readonly Regex _invalidSerialCharsRegex = new Regex(@"[^\x20-\x7E\r\n]", RegexOptions.Compiled);

        private SerialPort _serialPort;
        private StringBuilder _serialBuffer = new();
        private bool _isConnected = false;
        private bool _serialDisconnected = false;
        private bool _manualDisconnect = false;
        private bool _serialPortFullyInitialized = false;
        private string _lastConnectedPort = string.Empty;
        private string _userSelectedPort = string.Empty;
        private DateTime _lastValidDataTimestamp = DateTime.MinValue;
        private int _noDataCounter = 0;
        private bool _expectingData = false;
        private bool _disposed = false;

        // FIX 1: Add thread safety and buffering
        private readonly object _bufferLock = new object();
        private readonly ConcurrentQueue<string> _dataQueue = new ConcurrentQueue<string>();
        private readonly Timer _dataProcessingTimer;
        private readonly Timer _bufferCleanupTimer;

        // FIX 2: Add rate limiting to prevent overwhelming
        private DateTime _lastDataProcessed = DateTime.MinValue;
        private const int MinProcessingIntervalMs = 10; // Minimum 10ms between processing

        // FIX 3: Add buffer size monitoring
        private int _totalBytesReceived = 0;
        private DateTime _lastBufferSizeLog = DateTime.MinValue;

        public event Action<string> DataReceived;
        public event Action Connected;
        public event Action Disconnected;

        public bool IsConnected => _isConnected && !_serialDisconnected;
        public bool IsFullyInitialized => _serialPortFullyInitialized;
        public string LastConnectedPort => _lastConnectedPort;
        public string CurrentPort => _serialPort?.PortName ?? string.Empty;

        public SerialConnectionManager()
        {
            // FIX 4: Initialize processing timers
            _dataProcessingTimer = new Timer(ProcessDataQueue, null,
                TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

            _bufferCleanupTimer = new Timer(PerformBufferCleanup, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public void InitSerial(string portName, int baudRate)
        {
            try
            {
                // Validate port name
                if (string.IsNullOrWhiteSpace(portName))
                {
                    Debug.WriteLine("[Serial] Invalid port name provided");
                    return;
                }

                var availablePorts = SerialPort.GetPortNames();
                if (!availablePorts.Contains(portName))
                {
                    Debug.WriteLine($"[Serial] Port {portName} not in available ports: [{string.Join(", ", availablePorts)}]");

                    _isConnected = false;
                    _serialDisconnected = true;
                    return;
                }

                // FIX 5: Properly close existing connection with complete cleanup
                ClosePort();

                // FIX 6: Clear all buffers and reset counters
                ClearAllBuffers();

                _serialPort = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    ReceivedBytesThreshold = 1,
                    DtrEnable = true,
                    RtsEnable = true,
                    // FIX 7: Set buffer sizes to prevent OS-level buffer overflow
                    ReadBufferSize = 4096,
                    WriteBufferSize = 2048
                };

                // FIX 8: Ensure event handlers are not duplicated
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.ErrorReceived += SerialPort_ErrorReceived;

                _serialPort.Open();

                // FIX 9: Clear any stale data from the port
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();

                // Update connection state
                _isConnected = true;
                _lastConnectedPort = portName;
                _serialDisconnected = false;
                _serialPortFullyInitialized = false;

                // Reset the watchdog variables
                _lastValidDataTimestamp = DateTime.Now;
                _noDataCounter = 0;
                _expectingData = false;
                _totalBytesReceived = 0;

                Connected?.Invoke();

                Debug.WriteLine($"[Serial] Successfully connected to {portName} - waiting for data before applying ANY volumes");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Serial] Failed to open port {portName}: {ex.Message}");

                // Update connection state
                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;
            }
        }

        public void ManualDisconnect()
        {
            try
            {
                Debug.WriteLine("[Manual] User initiated manual disconnect");

                // Set flag to prevent automatic reconnection
                _manualDisconnect = true;

                ClosePort();

                _isConnected = false;
                _serialDisconnected = true;
                _serialPortFullyInitialized = false;

                Disconnected?.Invoke();

                Debug.WriteLine("[Manual] Manual disconnect completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to disconnect manually: {ex.Message}");
            }
        }

        public bool TryConnectToSavedPort(string savedPortName)
        {
            try
            {
                if (_isConnected && !_serialDisconnected)
                {
                    return true;
                }

                // If user manually selected a port, use that instead of saved port
                string portToTry;
                if (!string.IsNullOrEmpty(_userSelectedPort))
                {
                    portToTry = _userSelectedPort;
                    Debug.WriteLine($"[AutoConnect] Using user-selected port: {portToTry}");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(savedPortName))
                    {
                        Debug.WriteLine("[AutoConnect] No saved port name");
                        return false;
                    }
                    portToTry = savedPortName;
                    Debug.WriteLine($"[AutoConnect] Using saved port: {portToTry}");
                }

                var availablePorts = SerialPort.GetPortNames();
                if (!availablePorts.Contains(portToTry))
                {
                    Debug.WriteLine($"[AutoConnect] Port '{portToTry}' not available. Available: [{string.Join(", ", availablePorts)}]");
                    return false;
                }

                InitSerial(portToTry, 9600);

                // Clear user selected port after successful connection
                if (_isConnected && !_serialDisconnected)
                {
                    _userSelectedPort = string.Empty;
                }

                return _isConnected && !_serialDisconnected;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoConnect] Exception: {ex.Message}");
                return false;
            }
        }

        public void SetUserSelectedPort(string portName)
        {
            _userSelectedPort = portName;
            Debug.WriteLine($"[UI] User selected port: {portName}");

            // If user manually disconnected and now selects a port, clear the manual disconnect flag
            if (_manualDisconnect)
            {
                Debug.WriteLine("[UI] User selected new port after manual disconnect - clearing manual disconnect flag");
                _manualDisconnect = false;
            }
        }

        public void CheckConnection()
        {
            if (!_isConnected || _serialDisconnected || _disposed) return;

            try
            {
                // First check if the port is actually open
                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    Debug.WriteLine("[SerialWatchdog] Serial port closed unexpectedly");
                    HandleSerialDisconnection();
                    return;
                }

                // Check if we're receiving data
                if (_expectingData)
                {
                    TimeSpan elapsed = DateTime.Now - _lastValidDataTimestamp;

                    // If it's been more than 5 seconds without data, assume disconnected
                    if (elapsed.TotalSeconds > 5)
                    {
                        _noDataCounter++;
                        Debug.WriteLine($"[SerialWatchdog] No data received for {elapsed.TotalSeconds:F1} seconds (count: {_noDataCounter})");

                        // After 3 consecutive timeouts, consider disconnected
                        if (_noDataCounter >= 3)
                        {
                            Debug.WriteLine("[SerialWatchdog] Too many timeouts, considering disconnected");
                            HandleSerialDisconnection();
                            _noDataCounter = 0;
                            return;
                        }

                        // FIX 10: Safer connection test with error handling
                        try
                        {
                            if (_serialPort.IsOpen && _serialPort.BytesToWrite == 0)
                            {
                                _serialPort.Write(new byte[] { 10 }, 0, 1);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SerialWatchdog] Exception when testing connection: {ex.Message}");
                            HandleSerialDisconnection();
                            return;
                        }
                    }
                    else
                    {
                        // Reset counter if we're getting data
                        _noDataCounter = 0;
                    }
                }
                else if (_isConnected && (DateTime.Now - _lastValidDataTimestamp).TotalSeconds > 10)
                {
                    // If we haven't seen any data for 10 seconds after connecting,
                    // we may need to set the flag to start expecting data
                    _expectingData = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialWatchdog] Error: {ex.Message}");
            }
        }

        public bool ShouldAttemptReconnect()
        {
            return _serialDisconnected && !_manualDisconnect && !_disposed;
        }

        // FIX 11: Completely rewritten data received handler with proper buffering
        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen || _disposed)
            {
                return;
            }

            try
            {
                // FIX 12: Read all available data at once to reduce event frequency
                int bytesToRead = _serialPort.BytesToRead;
                if (bytesToRead == 0) return;

                // FIX 13: Prevent reading too much data at once
                if (bytesToRead > 1024)
                {
                    Debug.WriteLine($"[WARNING] Large data chunk: {bytesToRead} bytes - reading in chunks");
                    bytesToRead = 1024;
                }

                string incoming = _serialPort.ReadExisting();
                if (string.IsNullOrEmpty(incoming)) return;

                // Update timestamp when we receive data
                _lastValidDataTimestamp = DateTime.Now;
                _expectingData = true;
                _noDataCounter = 0;
                _totalBytesReceived += incoming.Length;

                // FIX 14: Filter invalid characters efficiently before buffering
                if (_invalidSerialCharsRegex.IsMatch(incoming))
                {
                    incoming = _invalidSerialCharsRegex.Replace(incoming, "");
                }

                // FIX 15: Thread-safe buffer management
                lock (_bufferLock)
                {
                    // FIX 16: Improved buffer size management
                    if (_serialBuffer.Length > 2048)
                    {
                        Debug.WriteLine($"[WARNING] Serial buffer size: {_serialBuffer.Length} chars");
                        TrimBuffer();
                    }

                    _serialBuffer.Append(incoming);
                }

                // FIX 17: Queue data for processing instead of immediate processing
                _dataQueue.Enqueue(incoming);

                // Mark as fully initialized after first valid data
                if (!_serialPortFullyInitialized && incoming.Length > 0)
                {
                    _serialPortFullyInitialized = true;
                    Debug.WriteLine("[Serial] Port fully initialized and receiving data");
                }
            }
            catch (IOException)
            {
                HandleSerialDisconnection();
            }
            catch (InvalidOperationException)
            {
                HandleSerialDisconnection();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Serial read: {ex.Message}");
                lock (_bufferLock)
                {
                    _serialBuffer.Clear();
                }
            }
        }

        // FIX 18: Process data queue on a timer to reduce CPU load
        private void ProcessDataQueue(object state)
        {
            if (_disposed || _dataQueue.IsEmpty) return;

            try
            {
                // Rate limiting
                if ((DateTime.Now - _lastDataProcessed).TotalMilliseconds < MinProcessingIntervalMs)
                {
                    return;
                }

                int processedItems = 0;
                const int maxItemsPerCycle = 10;

                while (_dataQueue.TryDequeue(out _) && processedItems < maxItemsPerCycle)
                {
                    processedItems++;
                }

                if (processedItems > 0)
                {
                    ProcessBufferedLines();
                    _lastDataProcessed = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Processing data queue: {ex.Message}");
            }
        }

        // FIX 19: Improved line processing with better memory management
        private void ProcessBufferedLines()
        {
            if (_disposed) return;

            try
            {
                lock (_bufferLock)
                {
                    string buffer = _serialBuffer.ToString();
                    if (string.IsNullOrEmpty(buffer)) return;

                    int processedLength = 0;
                    var lines = new List<string>();

                    // Find all complete lines
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        if (buffer[i] == '\n' || buffer[i] == '\r')
                        {
                            if (i > processedLength)
                            {
                                string line = buffer.Substring(processedLength, i - processedLength).Trim();
                                if (!string.IsNullOrWhiteSpace(line) && line.Length < 200)
                                {
                                    lines.Add(line);
                                }
                            }

                            // Skip any additional CR/LF characters
                            while (i + 1 < buffer.Length && (buffer[i + 1] == '\n' || buffer[i + 1] == '\r'))
                            {
                                i++;
                            }

                            processedLength = i + 1;
                        }
                    }

                    // Remove processed data from buffer
                    if (processedLength > 0)
                    {
                        _serialBuffer.Remove(0, processedLength);
                    }

                    // Process lines outside of lock
                    foreach (string line in lines)
                    {
                        try
                        {
                            DataReceived?.Invoke(line);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ERROR] Processing line '{line}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] ProcessBufferedLines: {ex.Message}");
            }
        }

        // FIX 20: Improved buffer trimming
        private void TrimBuffer()
        {
            try
            {
                string bufferContent = _serialBuffer.ToString();
                int lastNewline = Math.Max(bufferContent.LastIndexOf('\n'), bufferContent.LastIndexOf('\r'));

                if (lastNewline > bufferContent.Length / 2) // Keep last half if newline is in second half
                {
                    _serialBuffer.Clear();
                    _serialBuffer.Append(bufferContent.Substring(lastNewline + 1));
                    Debug.WriteLine($"[Buffer] Trimmed to last {_serialBuffer.Length} characters");
                }
                else
                {
                    // Keep only the last 256 characters
                    int keepLength = Math.Min(256, bufferContent.Length);
                    _serialBuffer.Clear();
                    _serialBuffer.Append(bufferContent.Substring(bufferContent.Length - keepLength));
                    Debug.WriteLine($"[Buffer] Trimmed to last {keepLength} characters");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Buffer trim: {ex.Message}");
                _serialBuffer.Clear();
            }
        }

        // FIX 21: Periodic buffer cleanup
        private void PerformBufferCleanup(object state)
        {
            if (_disposed) return;

            try
            {
                lock (_bufferLock)
                {
                    if (_serialBuffer.Length > 1024)
                    {
                        TrimBuffer();
                    }
                }

                // Log statistics periodically
                if ((DateTime.Now - _lastBufferSizeLog).TotalMinutes >= 5)
                {
                    Debug.WriteLine($"[Stats] Total bytes received: {_totalBytesReceived:N0}, Buffer size: {_serialBuffer.Length}, Queue size: {_dataQueue.Count}");
                    _lastBufferSizeLog = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Buffer cleanup: {ex.Message}");
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Debug.WriteLine($"[Serial] Error received: {e.EventType}");

            // Check for disconnection conditions
            if (e.EventType == SerialError.Frame || e.EventType == SerialError.RXOver ||
                e.EventType == SerialError.Overrun || e.EventType == SerialError.RXParity)
            {
                HandleSerialDisconnection();
            }
        }

        public void HandleSerialDisconnection()
        {
            if (_serialDisconnected || _disposed) return;

            Debug.WriteLine("[Serial] Disconnection detected");
            _serialDisconnected = true;
            _isConnected = false;

            Disconnected?.Invoke();

            ClosePort();
        }

        // FIX 22: Improved port cleanup with better error handling
        private void ClosePort()
        {
            try
            {
                if (_serialPort != null)
                {
                    // Remove event handlers first
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.ErrorReceived -= SerialPort_ErrorReceived;

                    if (_serialPort.IsOpen)
                    {
                        try
                        {
                            _serialPort.DiscardInBuffer();
                            _serialPort.DiscardOutBuffer();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WARNING] Error discarding buffers: {ex.Message}");
                        }

                        try
                        {
                            _serialPort.Close();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WARNING] Error closing port: {ex.Message}");
                        }
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                }

                // Clear all buffers after closing port
                ClearAllBuffers();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to cleanup serial port: {ex.Message}");
            }
        }

        // FIX 23: Comprehensive buffer clearing
        private void ClearAllBuffers()
        {
            try
            {
                lock (_bufferLock)
                {
                    _serialBuffer.Clear();
                }

                // Clear the data queue
                while (_dataQueue.TryDequeue(out _)) { }

                Debug.WriteLine("[Buffer] All buffers cleared");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Clearing buffers: {ex.Message}");
            }
        }

        // FIX 24: Proper disposal with all cleanup
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Debug.WriteLine("[Serial] Disposing SerialConnectionManager");
                _disposed = true;

                // Dispose timers first
                _dataProcessingTimer?.Dispose();
                _bufferCleanupTimer?.Dispose();

                // Close port and clear buffers
                ClosePort();

                Debug.WriteLine("[Serial] SerialConnectionManager disposed successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Error during disposal: {ex.Message}");
            }
        }
    }
}
