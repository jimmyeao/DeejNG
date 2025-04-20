using System;
using System.Diagnostics;
using System.IO;
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
        public void ApplyMuteStateToTarget(string target, bool isMuted)
        {
            if (string.IsNullOrWhiteSpace(target)) return;

            if (target == "system")
            {
                var dev = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                dev.AudioEndpointVolume.Mute = isMuted;
            }
            else
            {
                var dev = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = dev.AudioSessionManager.Sessions;

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        string sessionId = session.GetSessionIdentifier?.ToLower() ?? "";
                        string instanceId = session.GetSessionInstanceIdentifier?.ToLower() ?? "";

                        if (sessionId.Contains(target) || instanceId.Contains(target))
                        {
                            session.SimpleAudioVolume.Mute = isMuted;
                        }
                    }
                    catch { }
                }
            }
        }

        public void ApplyVolumeToTarget(string executable, float level, bool isMuted = false)
        {
            level = Math.Clamp(level, 0.0f, 1.0f);

            if (string.IsNullOrWhiteSpace(executable) || executable.Trim().Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.Mute = isMuted;

                if (!isMuted)
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                }

                return;
            }

            var sessions = _deviceEnumerator
                .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                .AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    string sessionId = session.GetSessionIdentifier ?? "";
                    string instanceId = session.GetSessionInstanceIdentifier ?? "";

                    string procName = "";
                    try
                    {
                        int pid = (int)session.GetProcessID;
                        procName = Process.GetProcessById(pid).ProcessName.ToLowerInvariant();
                    }
                    catch
                    {
                        procName = ""; // fallback
                    }

                    // Strip file extensions and normalize for comparison
                    string cleanedExecutable = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
                    string cleanedProcName = Path.GetFileNameWithoutExtension(procName).ToLowerInvariant();

                    if (cleanedProcName.Equals(cleanedExecutable, StringComparison.OrdinalIgnoreCase))

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
                    Debug.WriteLine($"[ApplyVolume] Error processing session {i}: {ex.Message}");
                }
            }
        }


    }
}
