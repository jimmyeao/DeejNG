using DeejNG.Dialogs;
using DeejNG.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeejNG.Services
{
    /// <summary>
    /// Handles execution of button actions such as media control and mute toggling.
    /// </summary>
    public class ButtonActionHandler
    {
        #region Win32 API for Media Keys

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const int KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const int KEYEVENTF_KEYUP = 0x0002;

        // Virtual key codes for media keys
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const byte VK_MEDIA_STOP = 0xB2;

        #endregion

        private readonly List<ChannelControl> _channelControls;

        /// <summary>
        /// Initializes a new instance of the ButtonActionHandler class.
        /// </summary>
        /// <param name="channelControls">Reference to the channel controls for mute operations</param>
        public ButtonActionHandler(List<ChannelControl> channelControls)
        {
            _channelControls = channelControls ?? throw new ArgumentNullException(nameof(channelControls));
        }

        /// <summary>
        /// Executes the specified button action.
        /// </summary>
        /// <param name="mapping">The button mapping to execute</param>
        public void ExecuteAction(ButtonMapping mapping)
        {
            if (mapping == null || mapping.Action == ButtonAction.None)
                return;

#if DEBUG
            Debug.WriteLine($"[ButtonAction] Executing {mapping.Action} for button {mapping.ButtonIndex}");
#endif

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
#if DEBUG
                        Debug.WriteLine($"[ButtonAction] ToggleInputOutput not yet implemented");
#endif
                        break;

                    default:
#if DEBUG
                        Debug.WriteLine($"[ButtonAction] Unknown action: {mapping.Action}");
#endif
                        break;
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ButtonAction] Error executing {mapping.Action}: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// Simulates a media key press.
        /// </summary>
        private void SendMediaKey(byte keyCode)
        {
            // Press the key
            keybd_event(keyCode, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            // Release the key
            keybd_event(keyCode, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);

#if DEBUG
            Debug.WriteLine($"[ButtonAction] Sent media key: 0x{keyCode:X2}");
#endif
        }

        /// <summary>
        /// Toggles mute for a specific channel.
        /// </summary>
        private void ToggleChannelMute(int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= _channelControls.Count)
            {
#if DEBUG
                Debug.WriteLine($"[ButtonAction] Invalid channel index: {channelIndex}");
#endif
                return;
            }

            var channel = _channelControls[channelIndex];
            bool newMuteState = !channel.IsMuted;
            channel.SetMuted(newMuteState);

#if DEBUG
            Debug.WriteLine($"[ButtonAction] Toggled channel {channelIndex} mute to {newMuteState}");
#endif
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
                channel.SetMuted(newMuteState);
            }

#if DEBUG
            Debug.WriteLine($"[ButtonAction] Global mute set to {newMuteState}");
#endif
        }

    }
}
