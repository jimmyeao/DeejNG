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

        public void ApplyVolumeToTarget(string executable, float level, bool isMuted = false)
        {
            level = Math.Clamp(level, 0.0f, 1.0f);

            if (string.IsNullOrWhiteSpace(executable) || executable.Trim().Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"[System Mute] isMuted={isMuted}, level={level}");

                var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.Mute = isMuted;

                if (!isMuted)
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                }

                return;
            }

            // Normal session logic...
            var sessions = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    var sessionId = session.GetSessionIdentifier;
                    var instanceId = session.GetSessionInstanceIdentifier;

                    if ((!string.IsNullOrWhiteSpace(sessionId) && sessionId.Contains(executable, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(instanceId) && instanceId.Contains(executable, StringComparison.OrdinalIgnoreCase)))
                    {
                        session.SimpleAudioVolume.Mute = isMuted;

                        if (!isMuted)
                        {
                            session.SimpleAudioVolume.Volume = level;
                        }

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
