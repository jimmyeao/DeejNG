using DeejNG.Classes;
using DeejNG.Dialogs;
using DeejNG.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeejNG.Services
{
    /// <summary>
    /// Handles execution of button actions such as media control and mute toggling.
    /// </summary>
    public class ButtonActionHandler
    {

        #region Private Fields

        private const int KEYEVENTF_EXTENDEDKEY = 0x0001;

        private const int KEYEVENTF_KEYUP = 0x0002;

        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;

        // Virtual key codes for media keys
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

        private const byte VK_MEDIA_PREV_TRACK = 0xB1;

        private const byte VK_MEDIA_STOP = 0xB2;

        private readonly List<ChannelControl> _channelControls;

        private readonly DefaultAudioDeviceSwitcher _deviceSwitcher;

        private readonly AppSettingsManager _appSettingsManager;

        #endregion Private Fields

        #region Public Constructors

        /// <summary>
        /// Initializes a new instance of the ButtonActionHandler class.
        /// </summary>
        /// <param name="channelControls">Reference to the channel controls for mute operations</param>
        public ButtonActionHandler(List<ChannelControl> channelControls, DefaultAudioDeviceSwitcher deviceSwitcher, AppSettingsManager appSettingsManager)
        {
            _channelControls = channelControls ?? throw new ArgumentNullException(nameof(channelControls));
            _deviceSwitcher = deviceSwitcher ?? throw new ArgumentNullException(nameof(deviceSwitcher));
            _appSettingsManager = appSettingsManager ?? throw new ArgumentNullException(nameof(appSettingsManager));
        }

        #endregion Public Constructors

        #region Public Methods

        /// <summary>
        /// Executes the specified button action.
        /// </summary>
        /// <param name="mapping">The button mapping to execute</param>
        public void ExecuteAction(ButtonMapping mapping)
        {
            if (mapping == null || mapping.Action == ButtonAction.None)
                return;

            if (mapping.PressType == ButtonPressType.Short)
            {
                ExecuteShortPressAction(mapping);
            }
            else if (mapping.PressType == ButtonPressType.Long)
            {
                ExecuteLongPressAction(mapping);
            }
        }

        #endregion Public Methods

        #region Private Methods

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        /// <summary>
        /// Simulates a media key press.
        /// </summary>
        private void SendMediaKey(byte keyCode)
        {
            // Press the key
            keybd_event(keyCode, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            // Release the key
            keybd_event(keyCode, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);


        }

        /// <summary>
        /// Toggles mute for a specific channel.
        /// </summary>
        private void ToggleChannelMute(int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= _channelControls.Count)
            {

                return;
            }

            var channel = _channelControls[channelIndex];
            bool newMuteState = !channel.IsMuted;
            channel.SetMuted(newMuteState, applyToAudio: true);


        }

        /// <summary>
        /// Toggles mute for all channels.
        /// </summary>
        private void ToggleGlobalMute()
        {
            // Determine if any channel is unmuted
            bool anyUnmuted = false;
            foreach (var channel in _channelControls)
            {
                if (!channel.IsMuted)
                {
                    anyUnmuted = true;
                    break;
                }
            }

            // If any channel is unmuted, mute all. Otherwise, unmute all.
            bool newMuteState = anyUnmuted;

            foreach (var channel in _channelControls)
            {
                channel.SetMuted(newMuteState, applyToAudio: true);
            }
        }

        private void ExecuteShortPressAction(ButtonMapping mapping)
        {
            try
            {
                switch (mapping.Action)
                {
                    case ButtonAction.MediaPlayPause:
                        SendMediaKey(VK_MEDIA_PLAY_PAUSE);
                        break;

                    case ButtonAction.MediaNext:
                        SendMediaKey(VK_MEDIA_NEXT_TRACK);
                        break;

                    case ButtonAction.MediaPrevious:
                        SendMediaKey(VK_MEDIA_PREV_TRACK);
                        break;

                    case ButtonAction.MediaStop:
                        SendMediaKey(VK_MEDIA_STOP);
                        break;

                    case ButtonAction.MuteChannel:
                        ToggleChannelMute(mapping.TargetChannelIndex);
                        break;

                    case ButtonAction.GlobalMute:
                        ToggleGlobalMute();
                        break;

                    case ButtonAction.ToggleInputOutput:
                        // Not currently implemented - InputMode is not in ChannelControl

                        break;
                    case ButtonAction.ToggleFocusApplication:
                        ToggleCurrentFocusApplication(mapping.TargetChannelIndex);
                        break;

                    case ButtonAction.AudioDeviceSetupOne:
                        _deviceSwitcher.TrySetDefaultOutput(_appSettingsManager.AppSettings.AudioDeviceOneId, _appSettingsManager.AppSettings.AudioDeviceOneFriendlyName);
                        _deviceSwitcher.TrySetDefaultInput(_appSettingsManager.AppSettings.MicrophoneDeviceOneId, _appSettingsManager.AppSettings.MicrophoneDeviceOneFriendlyName);
                        break;

                    case ButtonAction.AudioDeviceSetupTwo:
                        _deviceSwitcher.TrySetDefaultOutput(_appSettingsManager.AppSettings.AudioDeviceTwoId, _appSettingsManager.AppSettings.AudioDeviceTwoFriendlyName);
                        _deviceSwitcher.TrySetDefaultInput(_appSettingsManager.AppSettings.MicrophoneDeviceTwoId, _appSettingsManager.AppSettings.MicrophoneDeviceTwoFriendlyName);
                        break;

                    case ButtonAction.AudioDeviceSetupThree:
                        _deviceSwitcher.TrySetDefaultOutput(_appSettingsManager.AppSettings.AudioDeviceThreeId, _appSettingsManager.AppSettings.AudioDeviceThreeFriendlyName);
                        _deviceSwitcher.TrySetDefaultInput(_appSettingsManager.AppSettings.MicrophoneDeviceThreeId, _appSettingsManager.AppSettings.MicrophoneDeviceThreeFriendlyName);
                        break;

                    default:

                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exeption trying to do button action: {ex}");
            }
        }

        private void ExecuteLongPressAction(ButtonMapping mapping)
        {
            try
            {
                switch (mapping.Action)
                {
                    case ButtonAction.MediaPlayPause:
                        SendMediaKey(VK_MEDIA_PLAY_PAUSE);
                        break;

                    case ButtonAction.MediaNext:
                        SendMediaKey(VK_MEDIA_NEXT_TRACK);
                        break;

                    case ButtonAction.MediaPrevious:
                        SendMediaKey(VK_MEDIA_PREV_TRACK);
                        break;

                    case ButtonAction.MediaStop:
                        SendMediaKey(VK_MEDIA_STOP);
                        break;

                    case ButtonAction.MuteChannel:
                        ToggleChannelMute(mapping.TargetChannelIndex);
                        break;

                    case ButtonAction.GlobalMute:
                        ToggleGlobalMute();
                        break;

                    case ButtonAction.ToggleInputOutput:
                        // Not currently implemented - InputMode is not in ChannelControl

                        break;
                    case ButtonAction.ToggleFocusApplication:
                        ToggleCurrentFocusApplication(mapping.TargetChannelIndex);
                        break;

                    case ButtonAction.AudioDeviceSetupOne:
                        _deviceSwitcher.TrySetDefaultOutput(_appSettingsManager.AppSettings.AudioDeviceOneId, _appSettingsManager.AppSettings.AudioDeviceOneFriendlyName);
                        _deviceSwitcher.TrySetDefaultInput(_appSettingsManager.AppSettings.MicrophoneDeviceOneId, _appSettingsManager.AppSettings.MicrophoneDeviceOneFriendlyName);
                        break;

                    case ButtonAction.AudioDeviceSetupTwo:
                        _deviceSwitcher.TrySetDefaultOutput(_appSettingsManager.AppSettings.AudioDeviceTwoId, _appSettingsManager.AppSettings.AudioDeviceTwoFriendlyName);
                        _deviceSwitcher.TrySetDefaultInput(_appSettingsManager.AppSettings.MicrophoneDeviceTwoId, _appSettingsManager.AppSettings.MicrophoneDeviceTwoFriendlyName);
                        break;

                    case ButtonAction.AudioDeviceSetupThree:
                        _deviceSwitcher.TrySetDefaultOutput(_appSettingsManager.AppSettings.AudioDeviceThreeId, _appSettingsManager.AppSettings.AudioDeviceThreeFriendlyName);
                        _deviceSwitcher.TrySetDefaultInput(_appSettingsManager.AppSettings.MicrophoneDeviceThreeId, _appSettingsManager.AppSettings.MicrophoneDeviceThreeFriendlyName);
                        break;

                    default:

                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exeption trying to do button action: {ex}");
            }
        }

        private void ToggleCurrentFocusApplication(int channelIndex)
        {
            string currentFocus = AudioUtilities.GetCurrentFocusAudioTarget();

            if (currentFocus == null || currentFocus.Equals(String.Empty))
            {
                Debug.WriteLine("No audio focus target detected.");
                return;
            }

            // This might potentially break because we get a reference of the list itself back, not sure if editing this will break other async code. Otherwise we have to clone it.
            var audioTargets = _channelControls[channelIndex].AudioTargets;

            if (audioTargets == null)
            {
                audioTargets = new List<AudioTarget>();
            }

            int removeIndex = -1;
            int count = 0;

            foreach (AudioTarget target in audioTargets)
            {

                if (currentFocus.Equals(target.Name))
                {
                    removeIndex = count;
                    break;
                }
                count++;
            }


            if (removeIndex == -1)
            {
                audioTargets.Add(new AudioTarget { Name = currentFocus, IsInputDevice = false, IsOutputDevice = false });
                AudioCueService.PlayEnableCue();
                Debug.WriteLine("Added " + currentFocus);
            }
            else
            {
                audioTargets.RemoveAt(removeIndex);
                AudioCueService.PlayDisableCue();
                Debug.WriteLine("Removed " + currentFocus);
            }

            _channelControls[channelIndex].AudioTargets = audioTargets;
            _channelControls[channelIndex].RaiseTargetChanged();
        }

        #endregion Private Methods

    }
}
