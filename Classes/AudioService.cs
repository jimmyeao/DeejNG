// AudioService.cs
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
                _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                return;
            }

            var sessionManager = _defaultDevice.AudioSessionManager;
            var sessions = sessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.GetSessionIdentifier.Contains(executable, StringComparison.OrdinalIgnoreCase) ||
                    session.GetSessionInstanceIdentifier.Contains(executable, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        session.SimpleAudioVolume.Volume = level;
                    }
                    catch
                    {
                        // Log or ignore errors applying volume
                    }
                }
            }
        }
    }
}
