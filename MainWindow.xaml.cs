// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeejNG.Dialogs;
using DeejNG.Services;
using NAudio.CoreAudioApi;

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        #region Private Fields

        private AudioService _audioService;

        private List<ChannelControl> _channelControls = new();

        private StringBuilder _serialBuffer = new();

        private SerialPort _serialPort;

        private DispatcherTimer _meterTimer;


        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            InitializeComponent();
            _audioService = new AudioService();
            LoadAvailablePorts();
            LoadSettings();
            _meterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _meterTimer.Tick += UpdateMeters;
            _meterTimer.Start();

        }

        #endregion Public Constructors

        #region Private Properties

        private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeejNG", "settings.json");

        #endregion Private Properties

        #region Protected Methods

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;

                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while closing serial port: {ex.Message}", "Cleanup Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion Protected Methods

        #region Private Methods

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (ComPortSelector.SelectedItem is string selectedPort)
            {
                InitSerial(selectedPort, 9600);
            }
        }

        private void GenerateSliders(int count)
        {
            SliderPanel.Children.Clear();
            _channelControls.Clear();

            var savedTargets = LoadSettingsFromDisk()?.Targets ?? new List<string>();

            for (int i = 0; i < count; i++)
            {
                var control = new ChannelControl();
                if (i == 0)
                {
                    control.SetTargetExecutable("system");
                }
                else if (i < savedTargets.Count)
                {
                    control.SetTargetExecutable(savedTargets[i]);
                }

                control.TargetChanged += (_, _) => SaveSettings();
                _channelControls.Add(control);
                SliderPanel.Children.Add(control);

                control.TargetChanged += (_, _) => SaveSettings();
            }
        }
        private void UpdateMeters(object? sender, EventArgs e)
        {
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;

            const float visualGain = 1.5f; // Boost perceived level for visual effect
            const float systemCalibrationFactor = 2.0f; // Boost for system volume to reach realistic levels

            for (int i = 0; i < _channelControls.Count; i++)
            {
                var ctrl = _channelControls[i];
                var target = ctrl.TargetExecutable?.Trim();

                if (string.IsNullOrWhiteSpace(target) || target.Equals("system", StringComparison.OrdinalIgnoreCase))
                {
                    // Get system volume level (0.0 - 1.0)
                    float systemVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;

                    // Get the peak value for system audio (0.0 - 1.0)
                    float peak = device.AudioMeterInformation.MasterPeakValue;

                    // Apply system volume scaling, then apply stronger calibration
                    float boostedPeak = Math.Min(peak * systemVolume * systemCalibrationFactor * visualGain, 1.0f);

                    // Update the meter
                    ctrl.UpdateAudioMeter(boostedPeak);
                }
                else
                {
                    bool sessionFound = false;

                    for (int j = 0; j < sessions.Count; j++)
                    {
                        var session = sessions[j];
                        try
                        {
                            var sessionId = session.GetSessionIdentifier;
                            var instanceId = session.GetSessionInstanceIdentifier;

                            if ((sessionId?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                (instanceId?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false))
                            {
                                // Session found, apply the meter
                                float peak = session.AudioMeterInformation.MasterPeakValue * session.SimpleAudioVolume.Volume;
                                float boosted = Math.Min(peak * visualGain, 1.0f);
                                ctrl.UpdateAudioMeter(boosted);
                                sessionFound = true;
                                break;
                            }
                        }
                        catch
                        {
                            // Ignore bad sessions
                        }
                    }

                    // If no session is found, reset the level to 0 (empty meter)
                    if (!sessionFound)
                    {
                        ctrl.UpdateAudioMeter(0); // Reset meter to 0 if no session is found
                    }
                }
            }
        }


        private void HandleSliderData(string data)
        {
            string[] parts = data.Split('|');

            Dispatcher.Invoke(() =>
            {
                if (_channelControls.Count != parts.Length)
                {
                    GenerateSliders(parts.Length);
                }

                for (int i = 0; i < parts.Length; i++)
                {
                    if (float.TryParse(parts[i].Trim(), out float level))
                    {
                        level = 1f - Math.Clamp(level / 1023f, 0f, 1f);

                        float currentVolume = _channelControls[i].CurrentVolume;
                        if (Math.Abs(currentVolume - level) >= 0.01f)
                        {
                            _channelControls[i].SmoothAndSetVolume(level);

                            var target = _channelControls[i].TargetExecutable?.Trim();
                            if (!string.IsNullOrEmpty(target))
                            {
                                _audioService.ApplyVolumeToTarget(target, level);
                            }
                        }
                    }
                }
            });
        }

        private void InitSerial(string portName, int baudRate)
        {
            try
            {
                _serialPort?.Close();
                _serialPort = new SerialPort(portName, baudRate);
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.Open();

            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open serial port {portName}: {ex.Message}", "Serial Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadAvailablePorts()
        {
            ComPortSelector.ItemsSource = SerialPort.GetPortNames();
            if (ComPortSelector.Items.Count > 0)
                ComPortSelector.SelectedIndex = 0;
        }

        private void LoadSettings()
        {
            var settings = LoadSettingsFromDisk();
            if (!string.IsNullOrWhiteSpace(settings?.PortName))
            {
                InitSerial(settings.PortName, 9600);
            }
        }

        private AppSettings LoadSettingsFromDisk()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json);
                }
            }
            catch { }
            return null;
        }

        private void SaveSettings()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var settings = new AppSettings
                {
                    PortName = _serialPort?.PortName ?? string.Empty,
                    Targets = _channelControls.Select(c => c.TargetExecutable?.Trim() ?? string.Empty).ToList()
                };

                var json = JsonSerializer.Serialize(settings);
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            try
            {
                string incoming = _serialPort.ReadExisting();
                _serialBuffer.Append(incoming);

                while (true)
                {
                    string buffer = _serialBuffer.ToString();
                    int newLineIndex = buffer.IndexOf('\n');
                    if (newLineIndex == -1) break;

                    string line = buffer.Substring(0, newLineIndex).Trim();
                    _serialBuffer.Remove(0, newLineIndex + 1);

                    Dispatcher.BeginInvoke(() => HandleSliderData(line));
                }
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }

        #endregion Private Methods

        #region Private Classes

        private class AppSettings
        {
            #region Public Properties

            public string PortName { get; set; }
            public List<string> Targets { get; set; } = new();

            #endregion Public Properties
        }

        #endregion Private Classes
    }
}
