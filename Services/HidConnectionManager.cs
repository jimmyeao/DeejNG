using HidSharp;
using System.Diagnostics;

namespace DeejNG.Services
{
    /// <summary>
    /// Reads RawHID input from the mixer device and exposes parsed binary state.
    /// Packet layout:
    /// 
    /// Report ID at buf[0]
    /// Payload starts at buf[1]
    /// 
    /// For each channel (5 total):
    ///   byte 0 = channel button command/state
    ///   byte 1 = packed volume byte
    ///            bit 7 = changed flag
    ///            bits 0..6 = slider value (0..127)
    /// 
    /// After the 5 channels (10 bytes total):
    ///   3 extra button bytes
    /// 
    /// Total payload used = 13 bytes
    /// HID report payload size = 64 bytes
    /// </summary>
    public sealed class HidConnectionManager : IDisposable
    {
        #region Constants
        // HID input report payload size, excluding report ID byte
        private const int PayloadSize = 64;

        // RawHID / HidSharp usually gives report ID in byte 0
        private const int ReportIdOffset = 1;

        // Retry delay while disconnected / after disconnect
        private const int RetryDelayMs = 500;

        #endregion

        #region Private Fields

        private HidDevice? _device;
        private HidStream? _stream;
        private CancellationTokenSource? _cts;
        private Task? _readerTask;

        private volatile bool _isConnected;

        // Match your device
        private int _vid = 0x2341;
        private int _pid = 0x8036;

        #endregion

        #region Events

        public event Action? Connected;
        public event Action? Disconnected;

        /// <summary>
        /// Full parsed mixer state.
        /// Sliders are raw 0..127 values.
        /// ChannelButtons and ExtraButtons are raw command/state bytes from firmware.
        /// </summary>
        public event Action<HidMixerState>? DataReceived;

        /// <summary>
        /// Optional convenience event for any button command byte received that doesn't belong to a channel button.
        /// </summary>
        public event Action<int, byte>? ExtraButtonCommandReceived;

        #endregion

        #region Public Properties

        public bool IsConnected => _isConnected;

        #endregion

        #region Lifecycle

        public HidConnectionManager()
        {
        }

        public void Init(int vid, int pid, bool autoStart = false)
        {
            Debug.WriteLine("before");

            if (_cts != null)
            {
                return;
            }

            Debug.WriteLine($"HidConnectionManager.Init called with VID={vid:X4} PID={pid:X4} AutoStart={autoStart}");

            _vid = vid;
            _pid = pid;

            if (autoStart)
            {
                Start();
            }
        }

        public void Start()
        {
            if (_cts != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReaderLoop(_cts.Token));
        }

        public void Stop()
        {
            if (_cts == null)
            {
                return;
            }

            try
            {
                _cts.Cancel();
                _readerTask?.Wait(500);
            }
            catch
            {
                // Ignore shutdown exceptions
            }
            finally
            {
                _readerTask = null;
                _cts.Dispose();
                _cts = null;
                CloseDevice();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        public void ChangeVidPid(int vid, int pid)
        {
            if (_vid == vid && _pid == pid)
            {
                return;
            }
            // Restart connection to apply new VID/PID
            Stop();

            _vid = vid;
            _pid = pid;

            Start();
        }

        #endregion

        #region Device Handling

        private void TryOpenDevice()
        {
            int actualVid = ParseStoredHexInt(_vid);
            int actualPid = ParseStoredHexInt(_pid);

            var device = DeviceList.Local.GetHidDevices(actualVid, actualPid).FirstOrDefault();
            if (device == null)
            {
                Debug.WriteLine($"HID device not found with VID={_vid:X4} PID={_pid:X4}");
                return;
            }

            HidStream stream = device.Open();
            stream.ReadTimeout = Timeout.Infinite;

            _device = device;
            _stream = stream;

            if (!_isConnected)
            {
                _isConnected = true;
                Connected?.Invoke();
            }
        }

        private void CloseDevice()
        {
            try
            {
                _stream?.Dispose();
            }
            catch
            {
                // Ignore dispose failures
            }

            _stream = null;
            _device = null;

            if (_isConnected)
            {
                _isConnected = false;
                Disconnected?.Invoke();
            }
        }

        #endregion

        #region Reader Loop

        private void ReaderLoop(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[65]; // default fallback

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_stream == null)
                    {
                        TryOpenDevice();

                        if (_stream == null)
                        {
                            Thread.Sleep(RetryDelayMs);
                            continue;
                        }

                        int inputReportLength = _device!.GetMaxInputReportLength();
                        buffer = new byte[inputReportLength];
                    }

                    int bytesRead = _stream.Read(buffer, 0, buffer.Length);

                    if (bytesRead <= 0)
                    {
                        continue;
                    }

                    // Need at least report ID byte + payload bytes
                    if (bytesRead < ReportIdOffset + PayloadSize)
                    {
                        continue;
                    }

                    ParseAndRaise(buffer);
                }
                catch (Exception)
                {
                    // Device unplugged, reset, unavailable, etc.
                    CloseDevice();
                    Thread.Sleep(RetryDelayMs);
                }
            }
        }

        #endregion

        #region Parsing

        private void ParseAndRaise(byte[] buffer)
        {
            // Byte 0 on a RawHidSharp report is usually the report ID, so payload starts at byte 1

            int _numSliders = buffer[1];
            int _numButtons = buffer[2];

            HidMixerState state = new HidMixerState(_numSliders, _numButtons);

            // Value keeps increasing as we parse through the payload, starting after the report ID and slider/button count bytes
            int continousIndex = 3;

            // For each slider
            for (int i = 0; i < _numSliders; i++)
            {
                byte packedVolume = buffer[continousIndex];

                state.Sliders[i] = packedVolume;

                continousIndex++;
            }

            for (int i = 0; i < _numButtons; i++)
            {
                byte buttonValue = buffer[continousIndex];

                state.Buttons[i] = buttonValue;
                continousIndex++;
            }
            //state.DebugPrint();
            DataReceived?.Invoke(state);
        }

        private static int ParseStoredHexInt(int value)
        {
            return Convert.ToInt32(value.ToString(), 16);
        }

        #endregion

    }

    /// <summary>
    /// Parsed mixer state from one HID packet.
    /// </summary>
    public readonly struct HidMixerState
    {
        public byte[] Sliders { get; }
        public byte[] Buttons { get; }

        public readonly int NumSliders;
        public readonly int NumButtons;

        public HidMixerState(int numSliders, int numButtons)
        {
            Sliders = new byte[numSliders];
            Buttons = new byte[numButtons];
            NumSliders = numSliders;
            NumButtons = numButtons;
        }

        /// <summary>
        /// Converts a raw 0..127 slider value to 0..1 float.
        /// </summary>
        public static float Normalize(byte sliderValue)
        {
            return sliderValue / 127f;
        }

        /// <summary>
        /// Writes a readable debug dump of the mixer state.
        /// </summary>
        public void DebugPrint()
        {
            try
            {
                Debug.WriteLine("===== HID MIXER STATE =====");

                Debug.WriteLine($"Sliders: {NumSliders}");
                Debug.WriteLine($"Buttons: {NumButtons}");

                Debug.WriteLine("-- Sliders --");

                for (int i = 0; i < NumSliders; i++)
                {
                    byte raw = Sliders[i];
                    float normalized = Normalize(raw);

                    Debug.WriteLine(
                        $"Slider[{i}]  Raw: {raw,3}   Normalized: {normalized:0.000}"
                    );
                }

                Debug.WriteLine("-- Buttons --");

                for (int i = 0; i < NumButtons; i++)
                {
                    byte value = Buttons[i];
                    Debug.WriteLine($"Button[{i}]  Value: {value}");
                }

                Debug.WriteLine("===========================\n");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HidMixerState.DebugPrint error: {ex}");
            }
        }

        /// <summary>
        /// Compact single-line debug output (very useful for fast streams).
        /// Example: S[12,64,127] B[0,1,0]
        /// </summary>
        public void DebugPrintCompact()
        {
            try
            {
                string sliders = string.Join(",", Sliders.Take(NumSliders));
                string buttons = string.Join(",", Buttons.Take(NumButtons));

                Debug.WriteLine($"S[{sliders}]  B[{buttons}]");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HidMixerState.DebugPrintCompact error: {ex}");
            }
        }
    }
}