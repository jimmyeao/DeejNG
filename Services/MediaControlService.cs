using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeejNG.Services
{
    public enum MediaControlResult
    {
        Success,
        NotSupported,
        Failed,
        NotFound
    }

    public class MediaControlService
    {
        #region Win32 API Declarations

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // Virtual key codes for media keys
        private const int VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const int VK_MEDIA_STOP = 0xB2;
        private const int VK_VOLUME_MUTE = 0xAD;

        // Windows messages
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_APPCOMMAND = 0x0319;
        private const uint APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
        private const uint APPCOMMAND_MEDIA_STOP = 13;

        #endregion

        private readonly Dictionary<string, ProcessInfo> _processCache = new();
        private readonly object _cacheLock = new object();
        private DateTime _lastRefresh = DateTime.MinValue;
        private const int CACHE_REFRESH_SECONDS = 5;

        // Known media applications that typically support media keys
        private readonly HashSet<string> _knownMediaApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "spotify", "vlc", "wmplayer", "groove", "itunes", "winamp",
            "aimp", "foobar2000", "musicbee", "mediamonkey", "potplayer",
            "chrome", "firefox", "edge", "opera", "brave", "vivaldi"
        };

        private class ProcessInfo
        {
            public Process Process { get; set; }
            public IntPtr MainWindowHandle { get; set; }
            public DateTime LastSeen { get; set; }
            public bool SupportsMediaKeys { get; set; }
        }

        public MediaControlService()
        {
            Debug.WriteLine("[MediaControl] Initialized with Win32 API support");
        }

        public async Task<MediaControlResult> PauseApplicationAsync(string processName)
        {
            return await TogglePlaybackAsync(processName, pause: true);
        }

        public async Task<MediaControlResult> PlayApplicationAsync(string processName)
        {
            return await TogglePlaybackAsync(processName, pause: false);
        }

        public async Task<bool> IsApplicationPausedAsync(string processName)
        {
            // For this implementation, we can't reliably detect pause state
            // We'll rely on the UI to track this state
            await Task.CompletedTask;
            return false;
        }

        public async Task<bool> SupportsMediaControlAsync(string processName)
        {
            await Task.CompletedTask;

            try
            {
                string cleanName = Path.GetFileNameWithoutExtension(processName).ToLowerInvariant();

                // Check if it's a known media application
                if (_knownMediaApps.Contains(cleanName))
                {
                    return true;
                }

                // Check if the process exists and has a main window
                var processInfo = GetProcessInfo(cleanName);
                if (processInfo != null && processInfo.MainWindowHandle != IntPtr.Zero)
                {
                    // For unknown apps, assume they might support media keys if they have a main window
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaControl] Error checking support for {processName}: {ex.Message}");
                return false;
            }
        }

        private async Task<MediaControlResult> TogglePlaybackAsync(string processName, bool pause)
        {
            await Task.CompletedTask;

            try
            {
                string cleanName = Path.GetFileNameWithoutExtension(processName).ToLowerInvariant();
                var processInfo = GetProcessInfo(cleanName);

                if (processInfo == null)
                {
                    Debug.WriteLine($"[MediaControl] Process not found: {cleanName}");
                    return MediaControlResult.NotFound;
                }

                bool success = false;

                // Try different methods based on the application
                if (_knownMediaApps.Contains(cleanName))
                {
                    success = await SendMediaKeyToApplication(processInfo, pause);
                }
                else
                {
                    // For unknown apps, try the global media key approach
                    success = SendGlobalMediaKey(pause);
                }

                if (success)
                {
                    Debug.WriteLine($"[MediaControl] {(pause ? "Paused" : "Resumed")} {cleanName}");
                    return MediaControlResult.Success;
                }
                else
                {
                    Debug.WriteLine($"[MediaControl] Failed to {(pause ? "pause" : "resume")} {cleanName}");
                    return MediaControlResult.Failed;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaControl] Error {(pause ? "pausing" : "playing")} {processName}: {ex.Message}");
                return MediaControlResult.Failed;
            }
        }

        private async Task<bool> SendMediaKeyToApplication(ProcessInfo processInfo, bool pause)
        {
            await Task.CompletedTask;

            try
            {
                // Store the currently focused window
                IntPtr originalForeground = GetForegroundWindow();

                // Bring the target application to foreground briefly
                SetForegroundWindow(processInfo.MainWindowHandle);

                // Small delay to ensure the window is focused
                await Task.Delay(50);

                // Send the media key
                bool success = false;

                // Try different methods
                // Method 1: PostMessage with WM_APPCOMMAND
                uint command = pause ? APPCOMMAND_MEDIA_PLAY_PAUSE : APPCOMMAND_MEDIA_PLAY_PAUSE;
                success = PostMessage(processInfo.MainWindowHandle, WM_APPCOMMAND, processInfo.MainWindowHandle,
                    new IntPtr(command << 16));

                if (!success)
                {
                    // Method 2: SendKeys approach
                    success = SendGlobalMediaKey(pause);
                }

                // Restore the original foreground window
                if (originalForeground != IntPtr.Zero)
                {
                    SetForegroundWindow(originalForeground);
                }

                return success;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaControl] Error sending media key: {ex.Message}");
                return false;
            }
        }

        private bool SendGlobalMediaKey(bool pause)
        {
            try
            {
                // Use SendKeys to send the media play/pause key
                // This works globally and doesn't require focusing specific windows
                SendKeys.SendWait("{MEDIA_PLAY_PAUSE}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaControl] Error sending global media key: {ex.Message}");

                // Fallback: Try using keybd_event
                try
                {
                    keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, 0); // Key down
                    keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 2, 0); // Key up
                    return true;
                }
                catch (Exception ex2)
                {
                    Debug.WriteLine($"[MediaControl] Fallback method also failed: {ex2.Message}");
                    return false;
                }
            }
        }

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        private ProcessInfo GetProcessInfo(string processName)
        {
            lock (_cacheLock)
            {
                // Check cache first
                if ((DateTime.Now - _lastRefresh).TotalSeconds < CACHE_REFRESH_SECONDS)
                {
                    if (_processCache.TryGetValue(processName, out var cachedInfo))
                    {
                        // Verify the process is still running
                        try
                        {
                            if (cachedInfo.Process != null && !cachedInfo.Process.HasExited)
                            {
                                return cachedInfo;
                            }
                        }
                        catch
                        {
                            // Process no longer valid
                            _processCache.Remove(processName);
                        }
                    }
                }

                // Refresh cache
                RefreshProcessCache();

                // Try again from refreshed cache
                _processCache.TryGetValue(processName, out var processInfo);
                return processInfo;
            }
        }

        private void RefreshProcessCache()
        {
            try
            {
                var processes = Process.GetProcesses();
                var newCache = new Dictionary<string, ProcessInfo>();

                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited) continue;

                        string processName = process.ProcessName.ToLowerInvariant();

                        // Only cache processes we might be interested in
                        if (_knownMediaApps.Contains(processName) || process.MainWindowHandle != IntPtr.Zero)
                        {
                            newCache[processName] = new ProcessInfo
                            {
                                Process = process,
                                MainWindowHandle = process.MainWindowHandle,
                                LastSeen = DateTime.Now,
                                SupportsMediaKeys = _knownMediaApps.Contains(processName)
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MediaControl] Error processing {process.ProcessName}: {ex.Message}");
                        continue;
                    }
                }

                // Clear old cache and update
                foreach (var oldProcess in _processCache.Values)
                {
                    try
                    {
                        if (oldProcess.Process != null && !newCache.ContainsValue(oldProcess))
                        {
                            oldProcess.Process.Dispose();
                        }
                    }
                    catch { }
                }

                _processCache.Clear();
                foreach (var kvp in newCache)
                {
                    _processCache[kvp.Key] = kvp.Value;
                }

                _lastRefresh = DateTime.Now;
                Debug.WriteLine($"[MediaControl] Refreshed process cache with {newCache.Count} processes");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaControl] Error refreshing process cache: {ex.Message}");
            }
        }

        public void Dispose()
        {
            lock (_cacheLock)
            {
                foreach (var processInfo in _processCache.Values)
                {
                    try
                    {
                        processInfo.Process?.Dispose();
                    }
                    catch { }
                }
                _processCache.Clear();
            }
        }
    }
}