using DeejNG.Core.Interfaces;
using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeejNG.Services
{
    /// <summary>
    /// Manages a WebSocket connection to an OledDeej device.
    /// Sends channel config, volume state, and VU meter data.
    /// Receives snapshot-based volume/mute updates from the device.
    /// Protocol: the device sends absolute display values (0–100) at ≤20 Hz during
    /// user interaction. Each "update" message is an authoritative snapshot — the latest
    /// message always wins. The device does NOT send an unsolicited update on connect;
    /// the app must push config + state first.
    /// </summary>
    public sealed class WebSocketConnectionManager : IConnectionManager
    {
        #region Private Fields

        private const int ReceiveBufferSize = 4096;

        private CancellationTokenSource? _cts;
        private volatile bool _isConnected = false;
        private bool _manualDisconnect = false;
        private Task? _receiveLoop;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private string _host = "192.168.1.100";
        private int _port = 8765;
        private ClientWebSocket? _ws;

        #endregion Private Fields

        #region Public Events

        /// <summary>Raised on the thread-pool when the WebSocket connection is established.</summary>
        public event Action? Connected;

        /// <summary>Raised on the thread-pool when the WebSocket connection is lost.</summary>
        public event Action? Disconnected;

        /// <summary>
        /// Raised when the device sends an "update" message (encoder turned or button pressed).
        /// Each message is a snapshot: vol contains absolute display values 0–100 (not deltas),
        /// mute/bak/con contain per-channel toggle states. The latest message is always authoritative.
        /// Arrives at ≤20 Hz during active user interaction.
        /// bak = BACK button toggle state per channel (flips on each press).
        /// con = CONFIRM button toggle state per channel (flips on each press).
        /// Raised on the thread-pool — callers must Dispatcher.BeginInvoke to touch UI.
        /// </summary>
        public event Action<int[], bool[], bool[], bool[]>? UpdateReceived;

        #endregion Public Events

        #region Public Properties

        public bool IsConnected => _isConnected;

        /// <summary>True if a reconnect attempt should be made (i.e. not manually disconnected).</summary>
        public bool ShouldAttemptReconnect => !_manualDisconnect && !_isConnected;

        #endregion Public Properties

        #region Public Methods

        /// <summary>Set the host and port before calling ConnectAsync.</summary>
        public void Configure(string host, int port)
        {
            _host = host;
            _port = port;
        }

        /// <summary>Initiates connection to the device. Non-blocking — subscribe to Connected/Disconnected events.</summary>
        public async Task ConnectAsync(CancellationToken externalCt = default)
        {
            _manualDisconnect = false;

            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch { }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                var uri = new Uri($"ws://{_host}:{_port}/ws");

                Debug.WriteLine($"[WS] Connecting to {uri}");
                await _ws.ConnectAsync(uri, _cts.Token);

                _isConnected = true;
                _reconnectAttempt = 0;
                Debug.WriteLine("[WS] Connected");
                Connected?.Invoke();

                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WS] Connect failed: {ex.Message}");
                _isConnected = false;
                Disconnected?.Invoke();
            }
        }

        /// <summary>Gracefully disconnects and suppresses auto-reconnect.</summary>
        public void ManualDisconnect()
        {
            _manualDisconnect = true;
            DisconnectInternal();
        }

        /// <summary>Sends channel names, screensaver timeout, and encoder sensitivity to the device.</summary>
        public async Task SendConfigAsync(string[] names, int screensaverSeconds = 300, int encoderSensitivity = 4)
        {
            if (!_isConnected) return;
            var payload = JsonSerializer.Serialize(new { type = "config", names, screensaver = screensaverSeconds, sensitivity = encoderSensitivity });
            await SendRawAsync(payload);
        }

        /// <summary>
        /// Sends initial volume and mute state to the device.
        /// Must be called on connect (after SendConfigAsync) to seed the device's displays.
        /// The device will not send any updates until it has received config + state.
        /// </summary>
        public async Task SendStateAsync(int[] vols, bool[] mutes)
        {
            if (!_isConnected) return;
            var payload = JsonSerializer.Serialize(new { type = "state", vol = vols, mute = mutes });
            await SendRawAsync(payload);
        }

        /// <summary>
        /// Sends VU meter levels (0.0–1.0) to the device for display.
        /// Uses non-blocking lock acquisition — frames are dropped if the socket is busy,
        /// which prevents VU from blocking higher-priority state/config messages.
        /// </summary>
        public async Task SendVuAsync(float[] levels)
        {
            if (!_isConnected || _ws?.State != WebSocketState.Open) return;

            // Don't queue — drop this frame if the socket already has a send in flight
            if (!await _sendLock.WaitAsync(0)) return;

            try
            {
                if (_ws?.State != WebSocketState.Open) return;
                var rounded = Array.ConvertAll(levels, l => Math.Round(l, 2));
                var payload = JsonSerializer.Serialize(new { type = "vu", levels = rounded });
                var bytes = Encoding.UTF8.GetBytes(payload);
                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    _cts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WS] SendVuAsync failed: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            ManualDisconnect();
            _sendLock.Dispose();
        }

        #endregion Public Methods

        #region Private Methods

        private int _reconnectAttempt = 0;

        private void DisconnectInternal()
        {
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); } catch { }
            _isConnected = false;
        }

        private void HandleUnexpectedDisconnect()
        {
            if (!_isConnected) return;
            _isConnected = false;
            try { _ws?.Abort(); } catch { }
            Debug.WriteLine("[WS] Disconnected unexpectedly");
            Disconnected?.Invoke();
        }

        private void ProcessMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                if (typeEl.GetString() != "update") return;

                if (!root.TryGetProperty("vol", out var volEl)) return;
                if (!root.TryGetProperty("mute", out var muteEl)) return;

                var volArr = new int[volEl.GetArrayLength()];
                int i = 0;
                foreach (var el in volEl.EnumerateArray())
                    volArr[i++] = el.GetInt32();

                var muteArr = new bool[muteEl.GetArrayLength()];
                i = 0;
                foreach (var el in muteEl.EnumerateArray())
                    muteArr[i++] = el.GetBoolean();

                // bak = BACK button toggle state (optional — older firmware may omit)
                bool[] bakArr = new bool[volArr.Length];
                if (root.TryGetProperty("bak", out var bakEl))
                {
                    i = 0;
                    foreach (var el in bakEl.EnumerateArray())
                        if (i < bakArr.Length) bakArr[i++] = el.GetBoolean();
                }

                // con = CONFIRM button toggle state (optional — older firmware may omit)
                bool[] conArr = new bool[volArr.Length];
                if (root.TryGetProperty("con", out var conEl))
                {
                    i = 0;
                    foreach (var el in conEl.EnumerateArray())
                        if (i < conArr.Length) conArr[i++] = el.GetBoolean();
                }

                UpdateReceived?.Invoke(volArr, muteArr, bakArr, conArr);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WS] Failed to parse message: {ex.Message}");
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[ReceiveBufferSize];
            var segment = new ArraySegment<byte>(buffer);
            var accum = new StringBuilder();

            try
            {
                while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(segment, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        HandleUnexpectedDisconnect();
                        return;
                    }

                    accum.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        ProcessMessage(accum.ToString());
                        accum.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Clean shutdown — expected
            }
            catch (WebSocketException ex)
            {
                Debug.WriteLine($"[WS] WebSocketException in receive loop: {ex.Message}");
                HandleUnexpectedDisconnect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WS] Exception in receive loop: {ex.Message}");
                HandleUnexpectedDisconnect();
            }
        }

        private async Task SendRawAsync(string json)
        {
            if (_ws?.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync();
            try
            {
                if (_ws?.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    _cts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WS] SendRawAsync failed: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        #endregion Private Methods
    }
}
