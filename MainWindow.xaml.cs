// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeejNG.Dialogs;
using DeejNG.Services;

namespace DeejNG
{
    public partial class MainWindow : Window
    {
        private SerialPort _serialPort;
        private AudioService _audioService;
        private List<ChannelControl> _channelControls = new();
        private StringBuilder _serialBuffer = new();

        public MainWindow()
        {
            InitializeComponent();
            _audioService = new AudioService();
            LoadAvailablePorts();
            LoadSettings();
        }

        private void LoadAvailablePorts()
        {
            ConnectionStatus.Text = "Disconnected";
            ComPortSelector.ItemsSource = SerialPort.GetPortNames();
            if (ComPortSelector.Items.Count > 0)
                ComPortSelector.SelectedIndex = 0;
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            ConnectionStatus.Text = "Connecting...";
            if (ComPortSelector.SelectedItem is string selectedPort)
            {
                InitSerial(selectedPort, 9600);
            }
        }

        private void InitSerial(string portName, int baudRate)
        {
            SaveSettings(portName);
            try
            {
                _serialPort?.Close();
                _serialPort = new SerialPort(portName, baudRate);
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.Open();
                Dispatcher.Invoke(() => ConnectionStatus.Text = $"Connected to {portName}");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ConnectionStatus.Text = "Disconnected");
                MessageBox.Show($"Failed to open serial port {portName}: {ex.Message}", "Serial Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                        if (i == 4) Console.WriteLine($"Raw slider 5 input: {parts[i]}");

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

        private List<string> _pendingTargets = new();

        private void GenerateSliders(int count)
        {
            SliderPanel.Children.Clear();
            _channelControls.Clear();

            for (int i = 0; i < count; i++)
            {
                var control = new ChannelControl();

                if (i == 0)
                {
                    control.SetTargetExecutable("system");
                }
                else if (i < _pendingTargets.Count)
                {
                    control.SetTargetExecutable(_pendingTargets[i]);
                }

                _channelControls.Add(control);
                SliderPanel.Children.Add(control);
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
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

        private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeejNG", "settings.json");

        private class AppSettings
        {
            public string PortName { get; set; }
            public List<string> Targets { get; set; } = new();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

                    if (!string.IsNullOrWhiteSpace(settings?.PortName))
                    {
                        Dispatcher.InvokeAsync(() => InitSerial(settings.PortName, 9600));
                    }

                    _pendingTargets = settings?.Targets ?? new List<string>();
                }
            }
            catch { }
        }

      

            private void SaveSettings(string portName)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var settings = new AppSettings
            {
                PortName = portName,
                Targets = _channelControls.Select(c => c.TargetExecutable?.Trim() ?? "").ToList()
            };

            var json = System.Text.Json.JsonSerializer.Serialize(settings);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
          
    }
}
