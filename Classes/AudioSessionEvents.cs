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

        public void OnStateChanged(AudioSessionState state) { }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) { }
    }
}
