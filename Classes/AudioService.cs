using System;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace DeejNG.Services
{
    public class AudioService
    {
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private readonly MMDevice _defaultDevice;

        public AudioService()
        {
            _defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        public void ApplyVolumeToTarget(string executable, float level)
        {
            level = Math.Clamp(level, 0.0f, 1.0f);

            if (string.IsNullOrWhiteSpace(executable) || executable.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                var freshDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                freshDevice.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                return;
            }

            var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    var sessionId = session.GetSessionIdentifier;
                    var instanceId = session.GetSessionInstanceIdentifier;

                    Debug.WriteLine($"[Session {i}] ID: {sessionId}\nInstance: {instanceId}");

                    if (!string.IsNullOrWhiteSpace(sessionId) && sessionId.Contains(executable, StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrWhiteSpace(instanceId) && instanceId.Contains(executable, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[Match] Found match for '{executable}' in session {i}");
                        session.SimpleAudioVolume.Volume = level;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Error] Session {i}: {ex.Message}");
                }
            }
        }

    }
}
