using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
                    // Don't call StatusChanged - let MainWindow handle UI updates
                    return;
                }

                // Close existing connection if any
                ClosePort();

                _serialPort = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    ReceivedBytesThreshold = 1,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.ErrorReceived += SerialPort_ErrorReceived;

                _serialPort.Open();

                // Update connection state
                _isConnected = true;
                _lastConnectedPort = portName;
                _serialDisconnected = false;
                _serialPortFullyInitialized = false;

                // Reset the watchdog variables
                _lastValidDataTimestamp = DateTime.Now;
                _noDataCounter = 0;
                _expectingData = false;

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

                // Don't call StatusChanged - let MainWindow handle UI updates
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

                // Don't call StatusChanged - let MainWindow handle UI updates
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
            if (!_isConnected || _serialDisconnected) return;

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

                        // Try to write a single byte to test connection
                        try
                        {
                            _serialPort.Write(new byte[] { 10 }, 0, 1);
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
            return _serialDisconnected && !_manualDisconnect;
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                HandleSerialDisconnection();
                return;
            }

            try
            {
                string incoming = _serialPort.ReadExisting();

                // Update timestamp when we receive data
                _lastValidDataTimestamp = DateTime.Now;
                _expectingData = true;
                _noDataCounter = 0;

                // Buffer management to prevent long-running issues
                if (_serialBuffer.Length > 1024)
                {
                    string bufferContent = _serialBuffer.ToString();
                    int lastNewline = Math.Max(bufferContent.LastIndexOf('\n'), bufferContent.LastIndexOf('\r'));

                    if (lastNewline > 0)
                    {
                        _serialBuffer.Clear();
                        _serialBuffer.Append(bufferContent.Substring(lastNewline + 1));
                        Debug.WriteLine("[WARNING] Serial buffer trimmed to last valid line");
                    }
                    else
                    {
                        _serialBuffer.Clear();
                        Debug.WriteLine("[WARNING] Serial buffer exceeded limit and was cleared");
                    }
                }

                // Filter out non-printable characters and invalid data
                incoming = _invalidSerialCharsRegex.Replace(incoming, "");
                _serialBuffer.Append(incoming);

                // Process all complete lines in the buffer
                while (true)
                {
                    string buffer = _serialBuffer.ToString();
                    int newLineIndex = buffer.IndexOf('\n');
                    if (newLineIndex == -1)
                    {
                        newLineIndex = buffer.IndexOf('\r');
                        if (newLineIndex == -1) break;
                    }

                    string line = buffer.Substring(0, newLineIndex).Trim();

                    // Remove the processed line including any CR/LF characters
                    int removeLength = newLineIndex + 1;
                    if (buffer.Length > newLineIndex + 1)
                    {
                        if (buffer[newLineIndex] == '\r' && buffer[newLineIndex + 1] == '\n')
                            removeLength++;
                        else if (buffer[newLineIndex] == '\n' && buffer[newLineIndex + 1] == '\r')
                            removeLength++;
                    }

                    _serialBuffer.Remove(0, removeLength);

                    // Validate and process line
                    if (!string.IsNullOrWhiteSpace(line) && line.Length < 200)
                    {
                        DataReceived?.Invoke(line);

                        // Mark as fully initialized after first valid data
                        if (!_serialPortFullyInitialized)
                        {
                            _serialPortFullyInitialized = true;
                            Debug.WriteLine("[Serial] Port fully initialized and receiving data");
                        }
                    }
                }

                // Periodic buffer cleanup removed - was dead code (only cleared when buffer already empty)
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
                _serialBuffer.Clear();
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
            if (_serialDisconnected) return;

            Debug.WriteLine("[Serial] Disconnection detected");
            _serialDisconnected = true;
            _isConnected = false;

            // Don't call StatusChanged here - let the MainWindow handle UI updates
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
                        _serialPort.DiscardInBuffer();
                        _serialPort.DiscardOutBuffer();
                        _serialPort.Close();
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Failed to cleanup serial port: {ex.Message}");
            }
        }

        public void Dispose()
        {
            ClosePort();
            _serialBuffer.Clear();
        }
    }
}