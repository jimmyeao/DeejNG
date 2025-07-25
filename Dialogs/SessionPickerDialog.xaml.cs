using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using NAudio.CoreAudioApi;
using DeejNG.Classes; // Add this for AudioUtilities

namespace DeejNG.Dialogs
{
    public static class AudioSessionManagerHelper
    {
        #region Public Methods

        public static List<SessionInfo> GetSessionNames(List<string> alreadyUsed, string current)
        {
            var sessionList = new List<SessionInfo>
            {
                new SessionInfo { Id = "system", FriendlyName = "System" },
                new SessionInfo { Id = "unmapped", FriendlyName = "Unmapped Applications" }
            };

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                var seenFriendlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "system",
                    "unmapped"
                };

                // Add already used apps to seen list (except current)
                foreach (var used in alreadyUsed)
                {
                    if (!string.Equals(used, current, StringComparison.OrdinalIgnoreCase))
                        seenFriendlyNames.Add(used);
                }

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        // 🔥 FIX: Use safe process name retrieval
                        int processId;
                        try
                        {
                            processId = (int)session.GetProcessID;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SessionPicker] Failed to get process ID for session {i}: {ex.Message}");
                            continue;
                        }

                        // 🚀 USE EXISTING SAFE METHOD instead of direct Process access
                        string friendlyName = AudioUtilities.GetProcessNameSafely(processId);

                        if (string.IsNullOrEmpty(friendlyName))
                        {
                            Debug.WriteLine($"[SessionPicker] Could not get process name for PID {processId}");
                            continue;
                        }

                        // Only add if we haven't seen this app name before
                        if (!seenFriendlyNames.Contains(friendlyName))
                        {
                            sessionList.Add(new SessionInfo
                            {
                                Id = session.GetSessionIdentifier ??
                                 session.GetSessionInstanceIdentifier ??
                                 $"session_{processId}_{i}",
                                FriendlyName = friendlyName
                            });
                            seenFriendlyNames.Add(friendlyName);
                            Debug.WriteLine($"[SessionPicker] Added session: {friendlyName} (PID: {processId})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SessionPicker] Error processing session {i}: {ex.Message}");
                        continue;
                    }
                }

                Debug.WriteLine($"[SessionPicker] Found {sessionList.Count} total sessions ({sessionList.Count - 2} apps)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionPicker] Failed to enumerate audio sessions: {ex.Message}");
            }

            return sessionList;
        }

        #endregion Public Methods

        #region Public Classes

        public class SessionInfo
        {
            #region Public Properties

            public string FriendlyName { get; set; }
            public string Id { get; set; }

            #endregion Public Properties

            #region Public Methods

            public override string ToString() => FriendlyName;

            #endregion Public Methods
        }

        #endregion Public Classes
    }

    public partial class SessionPickerDialog : Window
    {
        #region Public Constructors

        public string? SelectedTarget { get; private set; }

        public SessionPickerDialog(bool isInputMode)
        {
            InitializeComponent();
            LoadSessions(isInputMode);
        }

        public SessionPickerDialog(string current)
        {
            InitializeComponent();

            try
            {
                var allTargets = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault()?.GetCurrentTargets() ?? new List<string>();
                var sessions = AudioSessionManagerHelper.GetSessionNames(allTargets, current);

                SessionComboBox.ItemsSource = sessions;
                SessionComboBox.DisplayMemberPath = "FriendlyName";
                SessionComboBox.SelectedValuePath = "Id";

                // Try to select the current session
                var currentSession = sessions.FirstOrDefault(s =>
                    string.Equals(s.FriendlyName, current, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Id, current, StringComparison.OrdinalIgnoreCase));

                if (currentSession != null)
                {
                    SessionComboBox.SelectedItem = currentSession;
                    Debug.WriteLine($"[SessionPicker] Selected current session: {current}");
                }
                else
                {
                    Debug.WriteLine($"[SessionPicker] Could not find current session: {current}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionPicker] Error in constructor: {ex.Message}");
                MessageBox.Show($"Error loading audio sessions: {ex.Message}", "Session Picker Error",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion Public Constructors

        #region Public Properties

        public string SelectedSession { get; private set; }

        #endregion Public Properties

        #region Private Methods

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SessionComboBox.SelectedItem is AudioSessionManagerHelper.SessionInfo selectedSession)
                {
                    SelectedSession = selectedSession.FriendlyName; // Use FriendlyName for consistency
                    Debug.WriteLine($"[SessionPicker] Selected: {SelectedSession}");
                }
                else if (!string.IsNullOrWhiteSpace(SessionComboBox.Text))
                {
                    SelectedSession = SessionComboBox.Text.Trim();
                    Debug.WriteLine($"[SessionPicker] Manual entry: {SelectedSession}");
                }
                else
                {
                    Debug.WriteLine("[SessionPicker] No selection made");
                    return;
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionPicker] Error in Ok_Click: {ex.Message}");
                MessageBox.Show($"Error selecting session: {ex.Message}", "Selection Error",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Keep the original LoadSessions method for input mode compatibility
        private void LoadSessions(bool isInputMode)
        {
            try
            {
                var items = new List<KeyValuePair<string, string>>();

                if (isInputMode)
                {
                    items.Add(new("System", "system"));

                    var devices = new MMDeviceEnumerator()
                        .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                    foreach (var device in devices)
                    {
                        string name = device.FriendlyName;
                        items.Add(new(name, name));
                    }
                }
                else
                {
                    items.Add(new("System", "system"));
                    items.Add(new("Unmapped Applications", "unmapped"));

                    var sessions = new MMDeviceEnumerator()
                        .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        .AudioSessionManager.Sessions;

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var session = sessions[i];
                            if (session == null) continue;

                            int processId;
                            try
                            {
                                processId = (int)session.GetProcessID;
                            }
                            catch
                            {
                                continue;
                            }

                            // 🔥 FIX: Use safe process name retrieval here too
                            string procName = AudioUtilities.GetProcessNameSafely(processId);

                            if (string.IsNullOrEmpty(procName))
                            {
                                procName = $"unknown_{processId}";
                            }

                            if (!seen.Contains(procName))
                            {
                                items.Add(new(procName, procName.ToLowerInvariant()));
                                seen.Add(procName);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SessionPicker] Error processing session {i} in LoadSessions: {ex.Message}");
                        }
                    }
                }

                SessionComboBox.ItemsSource = items;
                SessionComboBox.DisplayMemberPath = "Key";
                SessionComboBox.SelectedValuePath = "Value";

                if (items.Count > 0)
                    SessionComboBox.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionPicker] Error in LoadSessions: {ex.Message}");
                MessageBox.Show($"Error loading sessions: {ex.Message}", "Load Error",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion Private Methods
    }
}