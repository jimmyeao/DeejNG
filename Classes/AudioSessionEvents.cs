using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DeejNG.Classes
{
    public class AudioSessionEventsHandler : IAudioSessionEventsHandler
    {
        private readonly DeejNG.Dialogs.ChannelControl _control;

        public AudioSessionEventsHandler(DeejNG.Dialogs.ChannelControl control)
        {
            _control = control;
        }

        public void OnSimpleVolumeChanged(float volume, bool mute)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _control.SetMuted(mute);
            });
        }

        public void OnVolumeChanged(float volume, bool mute)
        {
            // Some NAudio versions may call this instead of or in addition to OnSimpleVolumeChanged
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _control.SetMuted(mute);
            });
        }

        public void OnDisplayNameChanged(string displayName) { }

        public void OnIconPathChanged(string iconPath) { }

        public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }

        public void OnGroupingParamChanged(ref Guid groupingId) { }

        public void OnStateChanged(AudioSessionState state)
        {
            // Handle session state changes
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    // Inform the control that the session expired
                    _control.HandleSessionExpired();
                });
            }
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // Log the disconnect reason for debugging
                System.Diagnostics.Debug.WriteLine($"[AudioSession] Session disconnected: {disconnectReason}");

                // Inform the control that the session disconnected
                _control.HandleSessionDisconnected();
            });
        }
    }
}