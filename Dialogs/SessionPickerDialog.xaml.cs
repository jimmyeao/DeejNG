using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using NAudio.CoreAudioApi;



namespace DeejNG.Dialogs
{
    public partial class SessionPickerDialog : Window
    {
        public string SelectedSession { get; private set; }

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

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (SessionComboBox.SelectedValue is string selectedId)
                SelectedSession = selectedId;
            else
                SelectedSession = SessionComboBox.Text;

            DialogResult = true;
            Close();
        }
    }


    public static class AudioSessionManagerHelper
    {
        public class SessionInfo
        {
            public string Id { get; set; }
            public string FriendlyName { get; set; }

            public override string ToString() => FriendlyName;
        }

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

      
    }

    public class SessionInfo
    {
        public string Id { get; set; }
        public string FriendlyName { get; set; }

        public override string ToString() => FriendlyName;
    }

}
