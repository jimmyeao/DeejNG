using DeejNG.Classes; // Add this for AudioUtilities
using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Windows;

namespace DeejNG.Dialogs
{
    public static class AudioSessionManagerHelper
    {

        #region Public Methods

        /// <summary>
        /// Retrieves a list of currently active audio session names for display or selection,
        /// excluding any that are already in use (except the current one).
        /// Also includes special entries for "System" and "Unmapped Applications".
        /// </summary>
        /// <param name="alreadyUsed">List of session names already assigned to other sliders or controls.</param>
        /// <param name="current">The current session name being edited (allowed even if already used).</param>
        /// <returns>List of <see cref="SessionInfo"/> representing selectable audio sessions.</returns>
        public static List<SessionInfo> GetSessionNames(List<string> alreadyUsed, string current)
        {
            // Initialize session list with special static entries
            var sessionList = new List<SessionInfo>
    {
        new SessionInfo { Id = "system", FriendlyName = "System" },
        new SessionInfo { Id = "unmapped", FriendlyName = "Unmapped Applications" }
    };

            try
            {
                // Get the default output audio device
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                // Retrieve all active sessions (e.g., apps playing sound)
                var sessions = device.AudioSessionManager.Sessions;

                // Track names we've already seen to avoid duplicates (case-insensitive)
                var seenFriendlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system",
            "unmapped"
        };

                // Mark already-used names as "seen", except for the current one being edited
                foreach (var used in alreadyUsed)
                {
                    if (!string.Equals(used, current, StringComparison.OrdinalIgnoreCase))
                        seenFriendlyNames.Add(used);
                }

                // Iterate through all active sessions
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        // Get the process ID safely; continue if it fails
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

                        // Use the utility method to safely get the process name
                        string friendlyName = AudioUtilities.GetProcessNameSafely(processId);

                        if (string.IsNullOrEmpty(friendlyName))
                        {
                            Debug.WriteLine($"[SessionPicker] Could not get process name for PID {processId}");
                            continue;
                        }

                        // Add only if this session name hasn't been added before
                        if (!seenFriendlyNames.Contains(friendlyName))
                        {
                            sessionList.Add(new SessionInfo
                            {
                                Id = session.GetSessionIdentifier ??
                                     session.GetSessionInstanceIdentifier ??
                                     $"session_{processId}_{i}", // fallback ID
                                FriendlyName = friendlyName
                            });

                            seenFriendlyNames.Add(friendlyName); // Mark as seen to prevent duplicates
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
                // If session enumeration fails entirely, log the error
                Debug.WriteLine($"[SessionPicker] Failed to enumerate audio sessions: {ex.Message}");
            }

            // Return the full list of sessions (starting with "System" and "Unmapped")
            return sessionList;
        }

        #endregion Public Methods

        #region Public Classes

        /// <summary>
        /// Represents basic information about an audio session,
        /// including a user-friendly name and an internal identifier.
        /// </summary>
        public class SessionInfo
        {
            #region Public Properties

            /// <summary>
            /// A user-friendly name for the session (e.g., "Spotify", "Chrome").
            /// Used for display purposes in the UI.
            /// </summary>
            public string FriendlyName { get; set; }

            /// <summary>
            /// A unique identifier for the session, typically used for matching
            /// or controlling the audio session internally.
            /// </summary>
            public string Id { get; set; }

            #endregion Public Properties

            #region Public Methods

            /// <summary>
            /// Returns the friendly name when this object is converted to a string.
            /// Useful for displaying in combo boxes or logs.
            /// </summary>
            public override string ToString() => FriendlyName;

            #endregion Public Methods
        }


        #endregion Public Classes

    }

    public partial class SessionPickerDialog : Window
    {

        #region Public Properties

        public string SelectedSession { get; private set; }
        public string? SelectedTarget { get; private set; }

        #endregion Public Properties

        #region Private Methods



        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                DragMove();
            }
        }

        /// <summary>
        /// Handles the OK button click in the session picker dialog.
        /// Assigns the selected session name based on either a selected item or manual entry,
        /// then closes the dialog with a success result.
        /// </summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Case 1: A predefined session was selected from the ComboBox
                if (SessionComboBox.SelectedItem is AudioSessionManagerHelper.SessionInfo selectedSession)
                {
                    // Store the user-selected session's friendly name
                    SelectedSession = selectedSession.FriendlyName;
                    Debug.WriteLine($"[SessionPicker] Selected: {SelectedSession}");
                }
                // Case 2: The user typed a custom session name into the ComboBox (manual entry)
                else if (!string.IsNullOrWhiteSpace(SessionComboBox.Text))
                {
                    // Store the trimmed manual entry
                    SelectedSession = SessionComboBox.Text.Trim();
                    Debug.WriteLine($"[SessionPicker] Manual entry: {SelectedSession}");
                }
                // Case 3: No selection or manual entry provided — do nothing
                else
                {
                    Debug.WriteLine("[SessionPicker] No selection made");
                    return;
                }

                // Set DialogResult to true so the parent window knows the user confirmed the selection
                DialogResult = true;

                // Close the dialog
                Close();
            }
            catch (Exception ex)
            {
                // Log and show any unexpected error
                Debug.WriteLine($"[SessionPicker] Error in Ok_Click: {ex.Message}");
                MessageBox.Show($"Error selecting session: {ex.Message}", "Selection Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion Private Methods
    }
}