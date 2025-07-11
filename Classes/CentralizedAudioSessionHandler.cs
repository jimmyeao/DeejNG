using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Diagnostics;

namespace DeejNG.Classes
{
    /// <summary>
    /// Centralized audio session event handler that dispatches events based on current configuration
    /// instead of being tied to a specific ChannelControl
    /// </summary>
    public class CentralizedAudioSessionHandler : IAudioSessionEventsHandler
    {
        /// <summary>
        /// Event fired when any audio session's volume or mute state changes
        /// </summary>
        public static event EventHandler<AudioSessionEventArgs> AudioSessionEvent;

        private readonly string _processName;
        private readonly int _processId;

        public CentralizedAudioSessionHandler(string processName, int processId)
        {
            _processName = processName;
            _processId = processId;
        }

        public void OnSimpleVolumeChanged(float volume, bool mute)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                AudioSessionEvent?.Invoke(this, new AudioSessionEventArgs
                {
                    ProcessName = _processName,
                    ProcessId = _processId,
                    Volume = volume,
                    IsMuted = mute,
                    EventType = AudioSessionEventType.VolumeChanged
                });
            });
        }

        public void OnVolumeChanged(float volume, bool mute)
        {
            // Some NAudio versions may call this instead of or in addition to OnSimpleVolumeChanged
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                AudioSessionEvent?.Invoke(this, new AudioSessionEventArgs
                {
                    ProcessName = _processName,
                    ProcessId = _processId,
                    Volume = volume,
                    IsMuted = mute,
                    EventType = AudioSessionEventType.VolumeChanged
                });
            });
        }

        public void OnDisplayNameChanged(string displayName) { }

        public void OnIconPathChanged(string iconPath) { }

        public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }

        public void OnGroupingParamChanged(ref Guid groupingId) { }

        public void OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    AudioSessionEvent?.Invoke(this, new AudioSessionEventArgs
                    {
                        ProcessName = _processName,
                        ProcessId = _processId,
                        EventType = AudioSessionEventType.SessionExpired
                    });
                });
            }
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Debug.WriteLine($"[CentralizedAudio] Session disconnected: {_processName} (PID: {_processId}) - {disconnectReason}");

                AudioSessionEvent?.Invoke(this, new AudioSessionEventArgs
                {
                    ProcessName = _processName,
                    ProcessId = _processId,
                    DisconnectReason = disconnectReason,
                    EventType = AudioSessionEventType.SessionDisconnected
                });
            });
        }
    }

    /// <summary>
    /// Event arguments for centralized audio session events
    /// </summary>
    public class AudioSessionEventArgs : EventArgs
    {
        public string ProcessName { get; set; }
        public int ProcessId { get; set; }
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public AudioSessionDisconnectReason DisconnectReason { get; set; }
        public AudioSessionEventType EventType { get; set; }
    }

    /// <summary>
    /// Types of audio session events
    /// </summary>
    public enum AudioSessionEventType
    {
        VolumeChanged,
        SessionExpired,
        SessionDisconnected
    }
}
