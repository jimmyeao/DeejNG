using DeejNG.Dialogs;
using DeejNG.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace DeejNG.Services
{
    /// <summary>
    /// Handles execution of button actions such as media control, mute toggling,
    /// keyboard shortcuts and shell commands.
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

        #endregion Private Fields

        #region Public Constructors

        /// <summary>
        /// Initializes a new instance of the ButtonActionHandler class.
        /// </summary>
        /// <param name="channelControls">Reference to the channel controls for mute operations</param>
        public ButtonActionHandler(List<ChannelControl> channelControls)
        {
            _channelControls = channelControls ?? throw new ArgumentNullException(nameof(channelControls));
        }

        #endregion Public Constructors

        #region Public Methods

        /// <summary>
        /// Executes the specified button action.
        /// </summary>
        /// <param name="mapping">The action binding to execute (a ButtonMapping or its LongPress binding)</param>
        public void ExecuteAction(ButtonActionBinding mapping)
        {
            if (mapping == null || mapping.Action == ButtonAction.None)
                return;

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

                    case ButtonAction.SendKeys:
                        SendKeyCombo(mapping.KeyCombo);
                        break;

                    case ButtonAction.RunCommand:
                        RunCommand(mapping.Command);
                        break;

                    case ButtonAction.ToggleInputOutput:
                        // Not currently implemented - InputMode is not in ChannelControl

                        break;

                    default:

                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Button] Failed to execute {mapping.Action}: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a key combo string like "Ctrl+Shift+M" into virtual-key codes
        /// (modifiers first, main key last). Returns false if the string is invalid.
        /// </summary>
        public static bool TryParseKeyCombo(string combo, out List<byte> virtualKeys)
        {
            virtualKeys = new List<byte>();
            if (string.IsNullOrWhiteSpace(combo)) return false;

            var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;

            foreach (var part in parts)
            {
                byte vk = part.ToLowerInvariant() switch
                {
                    "ctrl" or "control" => VK_CONTROL,
                    "shift" => VK_SHIFT,
                    "alt" => VK_MENU,
                    "win" or "windows" or "meta" => VK_LWIN,
                    _ => ParseKey(part)
                };

                if (vk == 0) return false;
                virtualKeys.Add(vk);
            }

            return true;
        }

        #endregion Public Methods

        #region Private Methods

        private const byte VK_CONTROL = 0x11;
        private const byte VK_SHIFT = 0x10;
        private const byte VK_MENU = 0x12;
        private const byte VK_LWIN = 0x5B;

        private static readonly KeyConverter _keyConverter = new();

        private static byte ParseKey(string name)
        {
            try
            {
                // Accept plain digits ("1") in addition to WPF names ("D1")
                if (name.Length == 1 && char.IsDigit(name[0]))
                    name = "D" + name;

                if (_keyConverter.ConvertFromInvariantString(name) is Key key && key != Key.None)
                    return (byte)KeyInterop.VirtualKeyFromKey(key);
            }
            catch { }

            return 0;
        }

        private static bool IsExtendedKey(byte vk) => vk switch
        {
            >= 0x21 and <= 0x28 => true,   // PageUp, PageDown, End, Home, arrows
            0x2D or 0x2E => true,          // Insert, Delete
            0x5B or 0x5C or 0x5D => true,  // Win keys, Apps
            0x6F or 0x90 => true,          // Numpad divide, NumLock
            >= 0xA6 and <= 0xB7 => true,   // Browser, volume, media and launch keys
            _ => false
        };

        /// <summary>
        /// Sends one or more key combos ("Ctrl+K, Ctrl+C"): presses modifiers and key, then releases in reverse order.
        /// </summary>
        private void SendKeyCombo(string comboList)
        {
            if (string.IsNullOrWhiteSpace(comboList)) return;

            foreach (var combo in comboList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseKeyCombo(combo, out var keys))
                {
                    Debug.WriteLine($"[Button] Invalid key combo: '{combo}'");
                    return;
                }

                foreach (var vk in keys)
                    keybd_event(vk, 0, IsExtendedKey(vk) ? (uint)KEYEVENTF_EXTENDEDKEY : 0, UIntPtr.Zero);

                for (int i = keys.Count - 1; i >= 0; i--)
                {
                    var vk = keys[i];
                    uint flags = KEYEVENTF_KEYUP | (IsExtendedKey(vk) ? (uint)KEYEVENTF_EXTENDEDKEY : 0);
                    keybd_event(vk, 0, flags, UIntPtr.Zero);
                }
            }
        }

        /// <summary>
        /// Runs a command line through cmd.exe without a console window.
        /// Works for executables, .bat/.cmd files, "powershell -File x.ps1", URLs via "start", etc.
        /// </summary>
        private void RunCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

            var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            using var process = Process.Start(psi);
        }

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

        #endregion Private Methods

    }
}
