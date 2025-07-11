using System;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DeejNG.Classes
{
    /// <summary>
    /// Decoupled audio session event handler that dynamically looks up the correct control
    /// instead of being tied to a specific ChannelControl instance
    /// </summary>
    public class DecoupledAudioSessionEventsHandler : IAudioSessionEventsHandler
    {
        private readonly MainWindow _mainWindow;
        private readonly string _targetName;

        public DecoupledAudioSessionEventsHandler(MainWindow mainWindow, string targetName)
        {
            _mainWindow = mainWindow;
            _targetName = targetName;
        }

        public void OnSimpleVolumeChanged(float volume, bool mute)
        {
            HandleMuteChange(mute);
        }

        public void OnVolumeChanged(float volume, bool mute)
        {
            // Some NAudio versions may call this instead of or in addition to OnSimpleVolumeChanged
            HandleMuteChange(mute);
        }

        private void HandleMuteChange(bool mute)
        {
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    // Dynamically find the current control for this target
                    var currentControl = _mainWindow.FindControlForTarget(_targetName);
                    if (currentControl != null)
                    {
                        currentControl.SetMuted(mute);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Handler] No control found for target: {_targetName}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Handler] Error handling mute change for {_targetName}: {ex.Message}");
            }
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
                    var currentControl = _mainWindow.FindControlForTarget(_targetName);
                    currentControl?.HandleSessionExpired();
                });
            }
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[AudioSession] Session disconnected: {disconnectReason} for {_targetName}");
                
                var currentControl = _mainWindow.FindControlForTarget(_targetName);
                currentControl?.HandleSessionDisconnected();
                
                // Notify MainWindow to clean up the handler
                _mainWindow.HandleSessionDisconnected(_targetName);
            });
        }
    }

    /// <summary>
    /// Legacy handler class - kept for compatibility but should be replaced with DecoupledAudioSessionEventsHandler
    /// </summary>
    [Obsolete("Use DecoupledAudioSessionEventsHandler instead for better app reassignment support")]
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
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    _control.HandleSessionExpired();
                });
            }
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[AudioSession] Session disconnected: {disconnectReason}");
                _control.HandleSessionDisconnected();
            });
        }
    }
}
