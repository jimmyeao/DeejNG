using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using NAudio.CoreAudioApi;

namespace DeejNG.Dialogs
{
    public static class AudioSessionManagerHelper
    {
        #region Public Methods

        public static List<SessionInfo> GetSessionNames(List<string> alreadyUsed, string current)
        {
            var sessionList = new List<SessionInfo>
            {
                new SessionInfo { Id = "system", FriendlyName = "System" }, // Always include system
                new SessionInfo { Id = "unmapped", FriendlyName = "Unmapped Applications" } // Add unmapped option
            };

            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;

            var seenFriendlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "system",
                "unmapped" // Don't duplicate these
            };

            foreach (var used in alreadyUsed)
            {
                if (!string.Equals(used, current, StringComparison.OrdinalIgnoreCase))
                    seenFriendlyNames.Add(used);
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                var session2 = session as AudioSessionControl;
                string id = session.GetSessionIdentifier ?? "(No ID)";
                string friendlyName = "(Unknown)";

                if (session2 != null)
                {
                    try
                    {
                        int processId = (int)session2.GetProcessID;
                        var process = Process.GetProcessById(processId);
                        friendlyName = Path.GetFileNameWithoutExtension(process.MainModule.FileName);
                    }
                    catch (Exception ex)
                    {
                        //Debug.WriteLine($"Failed to get process name: {ex.Message}");
                    }
                }

                if (!seenFriendlyNames.Contains(friendlyName))
                {
                    sessionList.Add(new SessionInfo
                    {
                        Id = id,
                        FriendlyName = friendlyName
                    });
                    seenFriendlyNames.Add(friendlyName);
                }
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
            LoadSessions(isInputMode); // This calls the bool version - keep as is
        }

        public SessionPickerDialog(string current)
        {
            InitializeComponent();

            var allTargets = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault()?.GetCurrentTargets() ?? new List<string>();
            var sessions = AudioSessionManagerHelper.GetSessionNames(allTargets, current);

            SessionComboBox.ItemsSource = sessions;
            SessionComboBox.DisplayMemberPath = "FriendlyName";
            SessionComboBox.SelectedValuePath = "Id";

            SessionComboBox.SelectedValue = current;
        }

        #endregion Public Constructors

        #region Public Properties

        public string SelectedSession { get; private set; }

        #endregion Public Properties

        #region Private Methods

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (SessionComboBox.SelectedValue is string selectedId)
                SelectedSession = selectedId;
            else
                SelectedSession = SessionComboBox.Text;

            DialogResult = true;
            Close();
        }

        // Keep the original LoadSessions method for input mode - DON'T change this one
        private void LoadSessions(bool isInputMode)
        {
            var items = new List<KeyValuePair<string, string>>();

            if (isInputMode)
            {
                // Always include "system" for microphone input control fallback (optional)
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
                // Always include system
                items.Add(new("System", "system"));

                // Add Unmapped Applications option for output mode
                items.Add(new("Unmapped Applications", "unmapped"));

                var sessions = new MMDeviceEnumerator()
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    .AudioSessionManager.Sessions;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        int pid = (int)session.GetProcessID;

                        string procName;
                        try
                        {
                            procName = Process.GetProcessById(pid).ProcessName;
                        }
                        catch
                        {
                            procName = "unknown";
                        }

                        if (!seen.Contains(procName))
                        {
                            items.Add(new($"{procName}", procName.ToLowerInvariant()));
                            seen.Add(procName);
                        }
                    }
                    catch { }
                }
            }

            SessionComboBox.ItemsSource = items;
            SessionComboBox.DisplayMemberPath = "Key";   // what the user sees
            SessionComboBox.SelectedValuePath = "Value"; // what we use internally

            if (items.Count > 0)
                SessionComboBox.SelectedIndex = 0;
        }

        #endregion Private Methods
    }
}