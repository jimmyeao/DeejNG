using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DeejNG.Models;
using NAudio.CoreAudioApi;

namespace DeejNG.Dialogs
{
    public partial class MultiTargetPickerDialog : Window
    {
        #region Public Constructors

        public MultiTargetPickerDialog(List<AudioTarget> currentTargets)
        {
            InitializeComponent();

            // Load current targets to mark as selected
            HashSet<string> selectedNames = new(
                currentTargets.Select(t => t.Name.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            LoadSessions(selectedNames);
            LoadInputDevices(selectedNames);
            LoadOutputDevices(selectedNames);

            AvailableSessionsListBox.ItemsSource = AvailableSessions;
            InputDevicesListBox.ItemsSource = InputDevices;
            OutputDevicesListBox.ItemsSource = OutputDevices;

            // Set up event handlers for checkbox changes
            var allItems = AvailableSessions.Concat(InputDevices).Concat(OutputDevices);
            foreach (var item in allItems)
            {
                item.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(SelectableSession.IsSelected))
                    {
                        UpdateSelectionStates();
                    }
                };
            }

            // Initial update
            UpdateSelectionStates();
        }

        #endregion Public Constructors

        #region Public Properties

        public ObservableCollection<SelectableSession> AvailableSessions { get; } = new();

        public ObservableCollection<SelectableSession> InputDevices { get; } = new();

        public ObservableCollection<SelectableSession> OutputDevices { get; } = new();

        public List<AudioTarget> SelectedTargets { get; private set; } = new();

        #endregion Public Properties

        #region Private Methods

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void LoadOutputDevices(HashSet<string> selectedNames)
        {
            try
            {
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in devices)
                {
                    string name = device.FriendlyName;
                    var newDevice = new SelectableSession
                    {
                        Id = name,
                        FriendlyName = name,
                        IsSelected = selectedNames.Contains(name.ToLowerInvariant()),
                        IsInputDevice = false,
                        IsOutputDevice = true
                    };

                    OutputDevices.Add(newDevice);
                }

                // Sort output devices alphabetically
                var sortedDevices = OutputDevices.OrderBy(d => d.FriendlyName).ToList();
                OutputDevices.Clear();
                foreach (var device in sortedDevices)
                {
                    OutputDevices.Add(device);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error loading output devices: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadInputDevices(HashSet<string> selectedNames)
        {
            try
            {
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                foreach (var device in devices)
                {
                    string name = device.FriendlyName;
                    var newDevice = new SelectableSession
                    {
                        Id = name,
                        FriendlyName = name,
                        IsSelected = selectedNames.Contains(name.ToLowerInvariant()),
                        IsInputDevice = true,
                        IsOutputDevice = false
                    };

                    InputDevices.Add(newDevice);
                }

                // Sort input devices alphabetically
                var sortedDevices = InputDevices.OrderBy(d => d.FriendlyName).ToList();
                InputDevices.Clear();
                foreach (var device in sortedDevices)
                {
                    InputDevices.Add(device);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error loading input devices: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadSessions(HashSet<string> selectedNames)
        {
            // Always add System first
            var systemSession = new SelectableSession
            {
                Id = "system",
                FriendlyName = "System",
                IsSelected = selectedNames.Contains("system"),
                IsInputDevice = false,
                IsOutputDevice = false
            };
            AvailableSessions.Add(systemSession);

            // Add Unmapped Applications option
            var unmappedSession = new SelectableSession
            {
                Id = "unmapped",
                FriendlyName = "Unmapped Applications",
                IsSelected = selectedNames.Contains("unmapped"),
                IsInputDevice = false,
                IsOutputDevice = false
            };
            AvailableSessions.Add(unmappedSession);

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seenProcesses.Add("system"); // Already added system
                seenProcesses.Add("unmapped"); // Already added unmapped

                // Get a fresh list of all running processes with audio sessions
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];

                    try
                    {
                        // Try to get the process information
                        int processId = (int)session.GetProcessID;
                        Process process = null;

                        try
                        {
                            process = Process.GetProcessById(processId);
                        }
                        catch (System.ArgumentException)
                        {
                            // Process might have exited - skip this one
                            continue;
                        }

                        string friendlyName = "";
                        try
                        {
                            // Try to get the file name
                            friendlyName = Path.GetFileNameWithoutExtension(process.MainModule.FileName);
                        }
                        catch
                        {
                            // If we can't get the file name, use the process name
                            friendlyName = process.ProcessName;
                        }

                        if (!string.IsNullOrWhiteSpace(friendlyName) && !seenProcesses.Contains(friendlyName))
                        {
                            var newSession = new SelectableSession
                            {
                                Id = friendlyName.ToLowerInvariant(),
                                FriendlyName = friendlyName,
                                IsSelected = selectedNames.Contains(friendlyName.ToLowerInvariant()),
                                IsInputDevice = false
                            };

                            AvailableSessions.Add(newSession);
                            seenProcesses.Add(friendlyName);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.WriteLine($"Failed to get process info: {ex.Message}");
                    }
                }

                // Sort the list: System first, then Unmapped, then alphabetical
                var sortedList = AvailableSessions.OrderBy(s =>
                {
                    if (s.Id == "system") return 0;
                    if (s.Id == "unmapped") return 1;
                    return 2;
                })
                    .ThenBy(s => s.FriendlyName)
                    .ToList();

                AvailableSessions.Clear();
                foreach (var session in sortedList)
                {
                    AvailableSessions.Add(session);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error loading audio sessions: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // Combine selected output and input targets
            SelectedTargets = new List<AudioTarget>();

            foreach (var session in AvailableSessions.Where(s => s.IsSelected))
            {
                SelectedTargets.Add(new AudioTarget
                {
                    Name = session.Id,
                    IsInputDevice = false,
                    IsOutputDevice = false
                });
            }

            foreach (var device in InputDevices.Where(d => d.IsSelected))
            {
                SelectedTargets.Add(new AudioTarget
                {
                    Name = device.Id,
                    IsInputDevice = true,
                    IsOutputDevice = false
                });
            }
            foreach (var device in OutputDevices.Where(d => d.IsSelected))
            {
                SelectedTargets.Add(new AudioTarget
                {
                    Name = device.Id,
                    IsInputDevice = false,
                    IsOutputDevice = true
                });
            }

            DialogResult = true;
            Close();
        }

        private void UpdateSelectionStates()
        {
            // Check if any item in each category is selected
            bool hasAppSelected = AvailableSessions.Any(s => s.IsSelected);
            bool hasInputSelected = InputDevices.Any(d => d.IsSelected);
            bool hasOutputSelected = OutputDevices.Any(d => d.IsSelected);

            // A slider can control apps, OR input devices, OR output devices, but not a mix.
            // Determine if each list should be enabled or disabled based on selections in other lists.
            bool appsEnabled = !hasInputSelected && !hasOutputSelected;
            bool inputsEnabled = !hasAppSelected && !hasOutputSelected;
            bool outputsEnabled = !hasAppSelected && !hasInputSelected;

            foreach (var session in AvailableSessions)
            {
                session.IsEnabled = appsEnabled;
            }

            foreach (var device in InputDevices)
            {
                device.IsEnabled = inputsEnabled;
            }

            foreach (var device in OutputDevices)
            {
                device.IsEnabled = outputsEnabled;
            }
        }

        #endregion Private Methods

        #region Public Classes

        public class SelectableSession : INotifyPropertyChanged
        {
            #region Private Fields

            private bool _isEnabled = true;
            private bool _isSelected;

            #endregion Private Fields

            #region Public Events

            public event PropertyChangedEventHandler PropertyChanged;

            #endregion Public Events

            #region Public Properties

            public string FriendlyName { get; set; }
            public string Id { get; set; }
            public bool IsEnabled
            {
                get => _isEnabled;
                set
                {
                    if (_isEnabled != value)
                    {
                        _isEnabled = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
                    }
                }
            }

            public bool IsInputDevice { get; set; }
            public bool IsOutputDevice { get; set; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected != value)
                    {
                        _isSelected = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                    }
                }
            }

            #endregion Public Properties
        }

        #endregion Public Classes
    }
}