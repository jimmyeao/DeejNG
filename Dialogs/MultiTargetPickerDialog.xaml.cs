using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
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
        }

        public ObservableCollection<SelectableSession> AvailableSessions { get; } = new();
        public ObservableCollection<SelectableSession> InputDevices { get; } = new();

        public List<AudioTarget> SelectedTargets { get; private set; } = new();

        public MultiTargetPickerDialog(List<AudioTarget> currentTargets)
        {
            InitializeComponent();

            // Load current targets to mark as selected
            HashSet<string> selectedNames = new(
                currentTargets.Select(t => t.Name.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            LoadSessions(selectedNames);
            LoadInputDevices(selectedNames);

            AvailableSessionsListBox.ItemsSource = AvailableSessions;
            InputDevicesListBox.ItemsSource = InputDevices;
        }

        private void LoadSessions(HashSet<string> selectedNames)
        {
            // Always add System
            AvailableSessions.Add(new SelectableSession
            {
                Id = "system",
                FriendlyName = "System",
                IsSelected = selectedNames.Contains("system"),
                IsInputDevice = false
            });

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seenProcesses.Add("system"); // Already added system

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
                            AvailableSessions.Add(new SelectableSession
                            {
                                Id = friendlyName.ToLowerInvariant(),
                                FriendlyName = friendlyName,
                                IsSelected = selectedNames.Contains(friendlyName.ToLowerInvariant()),
                                IsInputDevice = false
                            });
                            seenProcesses.Add(friendlyName);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.WriteLine($"Failed to get process info: {ex.Message}");
                    }
                }

                // Sort the list alphabetically
                var sortedList = AvailableSessions.OrderBy(s => s.Id == "system" ? 0 : 1)  // System first
                                                  .ThenBy(s => s.FriendlyName)            // Then alphabetical
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

        private void LoadInputDevices(HashSet<string> selectedNames)
        {
            try
            {
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                foreach (var device in devices)
                {
                    string name = device.FriendlyName;
                    InputDevices.Add(new SelectableSession
                    {
                        Id = name,
                        FriendlyName = name,
                        IsSelected = selectedNames.Contains(name.ToLowerInvariant()),
                        IsInputDevice = true
                    });
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