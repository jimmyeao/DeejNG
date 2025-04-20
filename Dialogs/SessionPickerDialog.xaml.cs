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
                new SessionInfo { Id = "system", FriendlyName = "System" } // Always include system
            };

            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;

            var seenFriendlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "system"
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

            var allTargets = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault()?.GetCurrentTargets() ?? new List<string>();
            var sessions = AudioSessionManagerHelper.GetSessionNames(allTargets, current);

            SessionComboBox.ItemsSource = sessions;
            SessionComboBox.DisplayMemberPath = "FriendlyName";
            SessionComboBox.SelectedValuePath = "Id";

            // Pre-select if it matches
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
        private void LoadSessions(bool isInputMode)
        {
            SessionComboBox.Items.Clear();

            if (isInputMode)
            {
                var devices = new MMDeviceEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                foreach (var device in devices)
                {
                    SessionComboBox.Items.Add(device.FriendlyName);
                }
            }
            else
            {
                var sessions = new MMDeviceEnumerator()
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    .AudioSessionManager.Sessions;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        string label = session.GetSessionIdentifier ?? $"PID {session.GetProcessID}";
                        SessionComboBox.Items.Add(label);
                    }
                    catch { }
                }
            }

            if (SessionComboBox.Items.Count > 0)
                SessionComboBox.SelectedIndex = 0;
        }


        #endregion Private Methods
    }
}
