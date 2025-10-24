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

        /// <summary>
        /// Initializes a new instance of the MultiTargetPickerDialog,
        /// pre-selecting any audio targets that are currently assigned.
        /// </summary>
        /// <param name="currentTargets">A list of currently selected audio targets to pre-mark as selected.</param>
        public MultiTargetPickerDialog(List<AudioTarget> currentTargets)
        {
            // Initialize the XAML UI components
            InitializeComponent();

            // Convert the list of current target names to a HashSet for fast lookups (case-insensitive)
            HashSet<string> selectedNames = new(
                currentTargets.Select(t => t.Name.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            // Load and pre-select matching sessions, input devices, and output devices
            LoadSessions(selectedNames);
            LoadInputDevices(selectedNames);
            LoadOutputDevices(selectedNames);

            // Separate manually specified apps (not in sessions/devices) and pre-populate the text box
            var manuallySpecifiedApps = new List<string>();
            foreach (var target in currentTargets)
            {
                // Skip special targets and devices
                if (target.IsInputDevice || target.IsOutputDevice ||
                    target.Name.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                    target.Name.Equals("unmapped", StringComparison.OrdinalIgnoreCase) ||
                    target.Name.Equals("current", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check if this target is in the running sessions list
                bool foundInSessions = AvailableSessions.Any(s =>
                    s.Id.Equals(target.Name, StringComparison.OrdinalIgnoreCase));

                // If not found in running sessions, it's a manually specified app
                if (!foundInSessions)
                {
                    // Remove .exe extension for cleaner display
                    string displayName = target.Name;
                    if (displayName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = displayName.Substring(0, displayName.Length - 4);
                    }
                    manuallySpecifiedApps.Add(displayName);

#if DEBUG
                    Debug.WriteLine($"[DialogLoad] Found manually specified app: {displayName} (original: {target.Name})");
#endif
                }
            }

            // Populate the manual app name text box with comma-separated list
            if (manuallySpecifiedApps.Count > 0)
            {
                ManualAppNameTextBox.Text = string.Join(", ", manuallySpecifiedApps);
#if DEBUG
                Debug.WriteLine($"[DialogLoad] Pre-populated manual apps: {ManualAppNameTextBox.Text}");
#endif
            }

            // Bind loaded collections to their corresponding list boxes in the UI
            AvailableSessionsListBox.ItemsSource = AvailableSessions;
            InputDevicesListBox.ItemsSource = InputDevices;
            OutputDevicesListBox.ItemsSource = OutputDevices;

            // Subscribe to property change events on all selectable items (sessions/devices)
            // This allows real-time updates when the user checks/unchecks a selection box
            var allItems = AvailableSessions.Concat(InputDevices).Concat(OutputDevices);
            foreach (var item in allItems)
            {
                item.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(SelectableSession.IsSelected))
                    {
                        // Update internal selection state whenever an item's IsSelected property changes
                        UpdateSelectionStates();
                    }
                };
            }

            // Perform initial sync of selection states for UI and logic
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

        /// <summary>
        /// Loads all active output (render) audio devices, wraps them in SelectableSession objects,
        /// and marks them as selected if they exist in the provided selection list.
        /// </summary>
        /// <param name="selectedNames">A set of names to pre-select in the list (case-insensitive).</param>
        private void LoadOutputDevices(HashSet<string> selectedNames)
        {
            try
            {
                // Get all active audio output devices (speakers, headphones, etc.)
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in devices)
                {
                    string name = device.FriendlyName;

                    // Wrap each device in a SelectableSession model for UI binding
                    var newDevice = new SelectableSession
                    {
                        Id = name, // Use the friendly name as a unique ID
                        FriendlyName = name,
                        IsSelected = selectedNames.Contains(name.ToLowerInvariant()), // Pre-select if in the selection set
                        IsInputDevice = false,
                        IsOutputDevice = true
                    };

                    // Add to the list of output devices shown in the UI
                    OutputDevices.Add(newDevice);
                }

                // Sort the output devices alphabetically by name for better UX
                var sortedDevices = OutputDevices.OrderBy(d => d.FriendlyName).ToList();
                OutputDevices.Clear();

                foreach (var device in sortedDevices)
                {
                    OutputDevices.Add(device);
                }
            }
            catch (System.Exception ex)
            {
                // Show a message box on failure, e.g. if device enumeration fails
                MessageBox.Show($"Error loading output devices: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Loads all active input (capture) audio devices (e.g., microphones),
        /// wraps them in SelectableSession objects, and pre-selects any that match the provided names.
        /// </summary>
        /// <param name="selectedNames">A set of device names that should be marked as selected.</param>
        private void LoadInputDevices(HashSet<string> selectedNames)
        {
            try
            {
                // Enumerate all active audio input devices (e.g., microphones, line-in)
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                foreach (var device in devices)
                {
                    string name = device.FriendlyName;

                    // Create a new selectable item representing the input device
                    var newDevice = new SelectableSession
                    {
                        Id = name, // Use friendly name as identifier
                        FriendlyName = name,
                        IsSelected = selectedNames.Contains(name.ToLowerInvariant()), // Pre-select if name matches
                        IsInputDevice = true,
                        IsOutputDevice = false
                    };

                    // Add to the collection of input devices for the UI
                    InputDevices.Add(newDevice);
                }

                // Sort the input devices alphabetically for better UX
                var sortedDevices = InputDevices.OrderBy(d => d.FriendlyName).ToList();
                InputDevices.Clear();

                foreach (var device in sortedDevices)
                {
                    InputDevices.Add(device);
                }
            }
            catch (System.Exception ex)
            {
                // Display an error dialog if device enumeration fails
                MessageBox.Show($"Error loading input devices: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Loads all current audio sessions and wraps them in SelectableSession objects for display.
        /// Adds special entries for "System", "Unmapped Applications" and "Currently Focused Application" and pre-selects any sessions
        /// that match the provided names.
        /// </summary>
        /// <param name="selectedNames">A set of target names that should be pre-selected in the list.</param>
        private void LoadSessions(HashSet<string> selectedNames)
        {
            // Always add "System" as a special session (represents master volume)
            var systemSession = new SelectableSession
            {
                Id = "system",
                FriendlyName = "System",
                IsSelected = selectedNames.Contains("system"),
                IsInputDevice = false,
                IsOutputDevice = false
            };
            AvailableSessions.Add(systemSession);

            // Add "Unmapped Applications" as a catch-all session
            var unmappedSession = new SelectableSession
            {
                Id = "unmapped",
                FriendlyName = "Unmapped Applications",
                IsSelected = selectedNames.Contains("unmapped"),
                IsInputDevice = false,
                IsOutputDevice = false
            };
            AvailableSessions.Add(unmappedSession);


            // Add "Currently Focused Application" as dynamic currently focused window session
            var currentSession = new SelectableSession
            {
                Id = "current",
                FriendlyName = "Currently Focused Application",
                IsSelected = selectedNames.Contains("current"),
                IsInputDevice = false,
                IsOutputDevice = false
            };
            AvailableSessions.Add(currentSession);

            try
            {
                // Get the default audio output device (used to get current sessions)
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                // Track process names already added to avoid duplicates
                var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seenProcesses.Add("system");    // Already manually added
                seenProcesses.Add("unmapped");  // Already manually added

                // Enumerate all active audio sessions
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];

                    try
                    {
                        // Attempt to get the owning process
                        int processId = (int)session.GetProcessID;
                        Process process = null;

                        try
                        {
                            process = Process.GetProcessById(processId);
                        }
                        catch (System.ArgumentException)
                        {
                            // The process may have exited; skip this session
                            continue;
                        }

                        string friendlyName = "";

                        try
                        {
                            // Try to extract the executable file name (without extension)
                            friendlyName = Path.GetFileNameWithoutExtension(process.MainModule.FileName);
                        }
                        catch
                        {
                            // If MainModule access fails (common for UWP or protected apps), fallback to process name
                            friendlyName = process.ProcessName;
                        }

                        // Add the session only if it hasn't already been added and has a name
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
                            seenProcesses.Add(friendlyName); // Mark as added
                        }
                    }
                    catch (System.Exception ex)
                    {
                        // Log session-level exceptions (safe fallback)
                        Debug.WriteLine($"Failed to get process info: {ex.Message}");
                    }
                }

                // Sort sessions: System first, Unmapped second, then remaining alphabetically
                var sortedList = AvailableSessions.OrderBy(s =>
                {
                    if (s.Id == "system") return 0;
                    if (s.Id == "unmapped") return 1;
                    return 2;
                })
                .ThenBy(s => s.FriendlyName) // Alphabetical order for regular apps
                .ToList();

                // Replace the original list with the sorted one
                AvailableSessions.Clear();
                foreach (var session in sortedList)
                {
                    AvailableSessions.Add(session);
                }
            }
            catch (System.Exception ex)
            {
                // Show error message if the entire session loading process fails
                MessageBox.Show($"Error loading audio sessions: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles the OK button click event. Combines all selected audio sessions,
        /// input devices, and output devices into the final list of selected targets,
        /// then closes the dialog with a success result.
        /// </summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // Initialize a new list to hold all selected audio targets
            SelectedTargets = new List<AudioTarget>();

            // Add manually specified applications first
            if (!string.IsNullOrWhiteSpace(ManualAppNameTextBox.Text))
            {
                var manualApps = ManualAppNameTextBox.Text
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(app => app.Trim())
                    .Where(app => !string.IsNullOrWhiteSpace(app));

                foreach (var appName in manualApps)
                {
                    // Normalize the name (add .exe if not present)
                    string normalizedName = appName;
                    if (!normalizedName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedName = appName + ".exe";
                    }

#if DEBUG
                    Debug.WriteLine($"[ManualTarget] Adding manually specified app: {normalizedName} (original: {appName})");
#endif

                    SelectedTargets.Add(new AudioTarget
                    {
                        Name = normalizedName,
                        IsInputDevice = false,
                        IsOutputDevice = false
                    });
                }
            }

            // Add selected sessions (e.g., applications) to the target list
            foreach (var session in AvailableSessions.Where(s => s.IsSelected))
            {
                SelectedTargets.Add(new AudioTarget
                {
                    Name = session.Id,
                    IsInputDevice = false,
                    IsOutputDevice = false
                });
            }

            // Add selected input devices (e.g., microphones) to the target list
            foreach (var device in InputDevices.Where(d => d.IsSelected))
            {
                SelectedTargets.Add(new AudioTarget
                {
                    Name = device.Id,
                    IsInputDevice = true,
                    IsOutputDevice = false
                });
            }

            // Add selected output devices (e.g., speakers, headphones) to the target list
            foreach (var device in OutputDevices.Where(d => d.IsSelected))
            {
                SelectedTargets.Add(new AudioTarget
                {
                    Name = device.Id,
                    IsInputDevice = false,
                    IsOutputDevice = true
                });
            }

            // Indicate success to the calling code (e.g., via ShowDialog)
            DialogResult = true;

            // Close the picker dialog
            Close();
        }

        /// <summary>
        /// Updates the enabled/disabled state of selectable items in the UI based on current selections.
        /// Enforces mutual exclusivity: a slider can only target apps, input devices, or output devices — not a combination.
        /// </summary>
        private void UpdateSelectionStates()
        {
            // Determine if any app sessions, input devices, or output devices are currently selected
            bool hasAppSelected = AvailableSessions.Any(s => s.IsSelected);
            bool hasInputSelected = InputDevices.Any(d => d.IsSelected);
            bool hasOutputSelected = OutputDevices.Any(d => d.IsSelected);

            // Determine whether each category should be enabled
            // A category is only enabled if no other category is selected
            bool appsEnabled = !hasInputSelected && !hasOutputSelected;
            bool inputsEnabled = !hasAppSelected && !hasOutputSelected;
            bool outputsEnabled = !hasAppSelected && !hasInputSelected;

            // Update IsEnabled state for app sessions
            foreach (var session in AvailableSessions)
            {
                session.IsEnabled = appsEnabled;
            }

            // Update IsEnabled state for input devices
            foreach (var device in InputDevices)
            {
                device.IsEnabled = inputsEnabled;
            }

            // Update IsEnabled state for output devices
            foreach (var device in OutputDevices)
            {
                device.IsEnabled = outputsEnabled;
            }
        }

        #endregion Private Methods

        #region Public Classes

        /// <summary>
        /// Represents a selectable audio session or device (input/output) for UI binding.
        /// Implements INotifyPropertyChanged to support reactive UI updates (e.g., checkboxes, enabled states).
        /// </summary>
        public class SelectableSession : INotifyPropertyChanged
        {
            #region Private Fields

            // Backing field for whether the item is currently enabled (e.g., interactable in the UI)
            private bool _isEnabled = true;

            // Backing field for whether the item is currently selected
            private bool _isSelected;

            #endregion Private Fields

            #region Public Events

            /// <summary>
            /// Raised when a property value changes. Used by WPF for two-way binding updates.
            /// </summary>
            public event PropertyChangedEventHandler PropertyChanged;

            #endregion Public Events

            #region Public Properties

            /// <summary>
            /// The friendly display name of the session or device (e.g., "Spotify", "Microphone").
            /// </summary>
            public string FriendlyName { get; set; }

            /// <summary>
            /// A unique identifier, typically derived from the process name or device name.
            /// </summary>
            public string Id { get; set; }

            /// <summary>
            /// Whether the item is currently enabled (used to disable categories if others are selected).
            /// </summary>
            public bool IsEnabled
            {
                get => _isEnabled;
                set
                {
                    if (_isEnabled != value)
                    {
                        _isEnabled = value;
                        // Notify UI of change so bindings can update
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
                    }
                }
            }

            /// <summary>
            /// Indicates whether this item represents an input device (e.g., microphone).
            /// </summary>
            public bool IsInputDevice { get; set; }

            /// <summary>
            /// Indicates whether this item represents an output device (e.g., speakers).
            /// </summary>
            public bool IsOutputDevice { get; set; }

            /// <summary>
            /// Whether the item is currently selected (e.g., checkbox checked).
            /// </summary>
            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected != value)
                    {
                        _isSelected = value;
                        // Notify UI of selection change
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                    }
                }
            }

            #endregion Public Properties
        }

        #endregion Public Classes
    }
}