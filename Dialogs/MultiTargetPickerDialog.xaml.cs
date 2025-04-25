// Update MultiTargetPickerDialog.xaml.cs 

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        public class SelectableSession
        {
            public string Id { get; set; }
            public string FriendlyName { get; set; }
            public bool IsSelected { get; set; }
            public bool IsInputDevice { get; set; }
            public bool IsAssignedElsewhere { get; set; }
            public string AssignmentInfo { get; set; }

            // Calculated properties to avoid needing converters
            public bool IsEnabled => !IsAssignedElsewhere;
            public Visibility AssignmentInfoVisibility => IsAssignedElsewhere ? Visibility.Visible : Visibility.Collapsed;
        }

        public ObservableCollection<SelectableSession> AvailableSessions { get; } = new();
        public ObservableCollection<SelectableSession> InputDevices { get; } = new();

        public List<AudioTarget> SelectedTargets { get; private set; } = new();
        private HashSet<string> _currentTargetNames;

        // Track selection mode - true for input mode, false for output mode, null for none selected yet
        private bool? _inputModeSelected = null;

        // Constructor simplified to take already-assigned app info
        public MultiTargetPickerDialog(List<AudioTarget> currentTargets)
        {
            InitializeComponent();

            // Store current target names for comparison
            _currentTargetNames = new HashSet<string>(
                currentTargets.Select(t => t.Name.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            // Set initial mode based on current targets
            if (currentTargets.Any())
            {
                _inputModeSelected = currentTargets.First().IsInputDevice;
            }

            // Load sessions and input devices
            LoadSessions();
            LoadInputDevices();

            // Set up UI
            AvailableSessionsListBox.ItemsSource = AvailableSessions;
            InputDevicesListBox.ItemsSource = InputDevices;

            // Add event handlers
            AvailableSessionsListBox.SelectionChanged += AvailableSessions_SelectionChanged;
            InputDevicesListBox.SelectionChanged += InputDevices_SelectionChanged;

            // Initialize UI state
            UpdateSelectionMode();
        }

        private void LoadSessions()
        {
            // Add system
            AvailableSessions.Add(new SelectableSession
            {
                Id = "system",
                FriendlyName = "System",
                IsSelected = _currentTargetNames.Contains("system"),
                IsInputDevice = false
            });

            try
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                var assignedApps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Build list of already assigned apps
                if (mainWindow != null)
                {
                    for (int i = 0; i < mainWindow._channelControls.Count; i++)
                    {
                        var controlTargets = mainWindow._channelControls[i].AudioTargets;
                        foreach (var target in controlTargets)
                        {
                            if (!target.IsInputDevice && !_currentTargetNames.Contains(target.Name.ToLowerInvariant()))
                            {
                                assignedApps[target.Name.ToLowerInvariant()] = i + 1;
                            }
                        }
                    }
                }

                // Now get audio sessions
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];

                    try
                    {
                        int processId = (int)session.GetProcessID;
                        var process = Process.GetProcessById(processId);
                        string friendlyName = Path.GetFileNameWithoutExtension(process.MainModule.FileName);
                        string targetId = friendlyName.ToLowerInvariant();

                        if (!seenProcesses.Contains(friendlyName))
                        {
                            bool isAlreadyAssigned = assignedApps.ContainsKey(targetId);
                            int sliderNumber = isAlreadyAssigned ? assignedApps[targetId] : -1;

                            // Disable or exclude already assigned apps
                            if (!isAlreadyAssigned || _currentTargetNames.Contains(targetId))
                            {
                                AvailableSessions.Add(new SelectableSession
                                {
                                    Id = targetId,
                                    FriendlyName = friendlyName,
                                    IsSelected = _currentTargetNames.Contains(targetId),
                                    IsInputDevice = false,
                                    AssignmentInfo = isAlreadyAssigned ? $"Already assigned to slider {sliderNumber}" : ""
                                });
                                seenProcesses.Add(friendlyName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to get process info: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading audio sessions: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void AvailableSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Block selection of apps assigned elsewhere
            foreach (var addedItem in e.AddedItems)
            {
                if (addedItem is SelectableSession session && session.IsAssignedElsewhere)
                {
                    // Prevent selecting this item by setting IsSelected back to false
                    session.IsSelected = false;
                    AvailableSessionsListBox.Items.Refresh();

                    MessageBox.Show(
                        $"'{session.FriendlyName}' is already assigned to slider {session.AssignmentInfo}.",
                        "Already Assigned",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    return;
                }
            }

            // When user selects an output app, check if any are now selected
            var anyOutputSelected = AvailableSessions.Any(s => s.IsSelected);

            if (anyOutputSelected && (_inputModeSelected == null || _inputModeSelected == false))
            {
                // Set mode to output
                _inputModeSelected = false;
                UpdateSelectionMode();
            }
            else if (!anyOutputSelected && _inputModeSelected == false)
            {
                // If no outputs selected now, allow changing mode
                _inputModeSelected = null;
                UpdateSelectionMode();
            }
        }
        private Dictionary<string, int> GetAssignedTargets(List<AudioTarget> currentTargets)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Get MainWindow instance
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return result;

            // Get all targets from all sliders
            for (int i = 0; i < mainWindow._channelControls.Count; i++)
            {
                var control = mainWindow._channelControls[i];
                var targets = control.AudioTargets;

                // Skip targets from the current control
                bool isCurrentControl = targets.Intersect(currentTargets).Any();
                if (isCurrentControl) continue;

                // Add all targets from this control to the dictionary
                foreach (var target in targets)
                {
                    result[target.Name] = i + 1; // +1 for human-readable slider number
                }
            }

            return result;
        }
        private void InputDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Block selection of devices assigned elsewhere
            foreach (var addedItem in e.AddedItems)
            {
                if (addedItem is SelectableSession session && session.IsAssignedElsewhere)
                {
                    // Prevent selecting this item by setting IsSelected back to false
                    session.IsSelected = false;
                    InputDevicesListBox.Items.Refresh();

                    MessageBox.Show(
                        $"'{session.FriendlyName}' is already assigned to slider {session.AssignmentInfo}.",
                        "Already Assigned",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    return;
                }
            }

            // When user selects an input device, check if any are now selected
            var anyInputSelected = InputDevices.Any(i => i.IsSelected);

            if (anyInputSelected && (_inputModeSelected == null || _inputModeSelected == true))
            {
                // Set mode to input
                _inputModeSelected = true;
                UpdateSelectionMode();
            }
            else if (!anyInputSelected && _inputModeSelected == true)
            {
                // If no inputs selected now, allow changing mode
                _inputModeSelected = null;
                UpdateSelectionMode();
            }
        }

        private void UpdateSelectionMode()
        {
            // Update UI based on selection mode
            if (_inputModeSelected == true)
            {
                // Input mode - disable output selection
                foreach (var session in AvailableSessions)
                {
                    if (!session.IsSelected)
                    {
                        session.IsSelected = false;
                    }
                }

                AvailableSessionsListBox.IsEnabled = false;
                InputDevicesListBox.IsEnabled = true;
            }
            else if (_inputModeSelected == false)
            {
                // Output mode - disable input selection
                foreach (var device in InputDevices)
                {
                    if (!device.IsSelected)
                    {
                        device.IsSelected = false;
                    }
                }

                AvailableSessionsListBox.IsEnabled = true;
                InputDevicesListBox.IsEnabled = false;
            }
            else
            {
                // No selection yet - both are enabled
                AvailableSessionsListBox.IsEnabled = true;
                InputDevicesListBox.IsEnabled = true;
            }

            // Force refresh of the ListBoxes
            AvailableSessionsListBox.Items.Refresh();
            InputDevicesListBox.Items.Refresh();
        }

        private void LoadSessions(Dictionary<string, int> assignedApps)
        {
            // Always add System
            bool isSystemAssigned = assignedApps.TryGetValue("system", out int systemSlider);
            bool isSystemCurrentlySelected = _currentTargetNames.Contains("system");

            AvailableSessions.Add(new SelectableSession
            {
                Id = "system",
                FriendlyName = "System",
                IsSelected = isSystemCurrentlySelected,
                IsInputDevice = false,
                IsAssignedElsewhere = isSystemAssigned && !isSystemCurrentlySelected,
                AssignmentInfo = isSystemAssigned ? systemSlider.ToString() : ""
            });

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];

                    try
                    {
                        int processId = (int)session.GetProcessID;
                        var process = Process.GetProcessById(processId);
                        string friendlyName = Path.GetFileNameWithoutExtension(process.MainModule.FileName);
                        string targetId = friendlyName.ToLowerInvariant();

                        if (!seenProcesses.Contains(friendlyName))
                        {
                            bool isAppAssigned = assignedApps.TryGetValue(targetId, out int sliderNumber);
                            bool isAppCurrentlySelected = _currentTargetNames.Contains(targetId);

                            AvailableSessions.Add(new SelectableSession
                            {
                                Id = targetId,
                                FriendlyName = friendlyName,
                                IsSelected = isAppCurrentlySelected,
                                IsInputDevice = false,
                                IsAssignedElsewhere = isAppAssigned && !isAppCurrentlySelected,
                                AssignmentInfo = isAppAssigned ? sliderNumber.ToString() : ""
                            });
                            seenProcesses.Add(friendlyName);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to get process info: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading audio sessions: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadInputDevices()
        {
            try
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                var assignedInputs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Build list of already assigned input devices
                if (mainWindow != null)
                {
                    for (int i = 0; i < mainWindow._channelControls.Count; i++)
                    {
                        var controlTargets = mainWindow._channelControls[i].AudioTargets;
                        foreach (var target in controlTargets)
                        {
                            if (target.IsInputDevice && !_currentTargetNames.Contains(target.Name.ToLowerInvariant()))
                            {
                                assignedInputs[target.Name.ToLowerInvariant()] = i + 1;
                            }
                        }
                    }
                }

                // Now get input devices
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                foreach (var device in devices)
                {
                    string name = device.FriendlyName;
                    string targetId = name.ToLowerInvariant();

                    bool isAlreadyAssigned = assignedInputs.ContainsKey(targetId);
                    int sliderNumber = isAlreadyAssigned ? assignedInputs[targetId] : -1;

                    // Disable or exclude already assigned devices
                    if (!isAlreadyAssigned || _currentTargetNames.Contains(targetId))
                    {
                        InputDevices.Add(new SelectableSession
                        {
                            Id = name,
                            FriendlyName = name,
                            IsSelected = _currentTargetNames.Contains(targetId),
                            IsInputDevice = true,
                            AssignmentInfo = isAlreadyAssigned ? $"Already assigned to slider {sliderNumber}" : ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading input devices: {ex.Message}", "Error",
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
                    IsInputDevice = false
                });
            }

            foreach (var device in InputDevices.Where(d => d.IsSelected))
            {
                SelectedTargets.Add(new AudioTarget
                {
                    Name = device.Id,
                    IsInputDevice = true
                });
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

}