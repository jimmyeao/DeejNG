using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;

namespace DeejNG.Classes
{
    /// <summary>
    /// Centralized utility class for audio-related operations to prevent code duplication
    /// </summary>
    public static class AudioUtilities
    {
        private static readonly Dictionary<int, string> _processNameCache = new();
        private static readonly HashSet<int> _systemProcessIds = new HashSet<int> { 0, 4, 8 };
        private static DateTime _lastProcessCacheCleanup = DateTime.MinValue;
        private static readonly object _cacheCleanupLock = new object();
        private const int MAX_PROCESS_CACHE_SIZE = 30;

        /// <summary>
        /// Safely gets the process name for a given process ID with caching and proper exception handling
        /// </summary>
        /// <param name="processId">The process ID to get the name for</param>
        /// <returns>The process name in lowercase, or empty string if not found/accessible</returns>
        public static string GetProcessNameSafely(int processId)
        {
            // Clean cache periodically (every 60 seconds)
            lock (_cacheCleanupLock)
            {
                if ((DateTime.Now - _lastProcessCacheCleanup).TotalSeconds > 60)
                {
                    CleanProcessCache();
                    _lastProcessCacheCleanup = DateTime.Now;
                }
            }

            // Check cache first
            if (_processNameCache.TryGetValue(processId, out string cachedName))
            {
                return cachedName;
            }

            // Skip system processes that cause Win32Exception
            if (_systemProcessIds.Contains(processId) || processId < 100)
            {
                _processNameCache[processId] = "";
                return "";
            }

            string processName = "";

            try
            {
                // Only use ProcessName property - never access MainModule to avoid Win32Exception
                using (var process = Process.GetProcessById(processId))
                {
                    if (process != null && !process.HasExited)
                    {
                        processName = process.ProcessName?.ToLowerInvariant() ?? "";
                    }
                }
            }
            catch
            {
                // Any exception - just return empty (includes ArgumentException, Win32Exception, etc.)
                processName = "";
            }

            // Cache the result
            _processNameCache[processId] = processName;
            return processName;
        }

        /// <summary>
        /// Finds an audio session that matches the given target name using optimized search
        /// </summary>
        /// <param name="sessions">The audio sessions to search through</param>
        /// <param name="targetName">The target application name to find</param>
        /// <returns>The matching audio session, or null if not found</returns>
        public static AudioSessionControl FindSessionOptimized(SessionCollection sessions, string targetName)
        {
            if (sessions == null || string.IsNullOrEmpty(targetName))
                return null;

            string normalizedTarget = targetName.ToLowerInvariant();

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    if (session == null) continue;

                    int processId = (int)session.GetProcessID;
                    string processName = GetProcessNameSafely(processId);

                    if (string.IsNullOrEmpty(processName)) continue;

                    // Check for direct match
                    if (processName.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return session;
                    }

                    // Check for partial matches in session identifiers as fallback
                    try
                    {
                        string sessionId = session.GetSessionIdentifier?.ToLowerInvariant() ?? "";
                        string instanceId = session.GetSessionInstanceIdentifier?.ToLowerInvariant() ?? "";

                        if (sessionId.Contains(normalizedTarget) || instanceId.Contains(normalizedTarget))
                        {
                            return session;
                        }
                    }
                    catch
                    {
                        // If we can't get session identifiers, skip this fallback
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioUtilities] Error processing session {i}: {ex.Message}");
                    continue;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the peak audio level for a specific session with error handling
        /// </summary>
        /// <param name="session">The audio session to get the peak level from</param>
        /// <returns>The peak level (0.0 to 1.0), or 0.0 if error</returns>
        public static float GetSessionPeakLevel(AudioSessionControl session)
        {
            if (session == null) return 0f;

            try
            {
                return session.AudioMeterInformation.MasterPeakValue;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// Checks if a session is muted with error handling
        /// </summary>
        /// <param name="session">The audio session to check</param>
        /// <returns>True if muted, false if not muted or error</returns>
        public static bool IsSessionMuted(AudioSessionControl session)
        {
            if (session == null) return false;

            try
            {
                return session.SimpleAudioVolume.Mute;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Safely sets the volume for a session with error handling
        /// </summary>
        /// <param name="session">The session to set volume for</param>
        /// <param name="volume">Volume level (0.0 to 1.0)</param>
        /// <param name="muted">Whether the session should be muted</param>
        /// <returns>True if successful, false if error</returns>
        public static bool SetSessionVolume(AudioSessionControl session, float volume, bool muted)
        {
            if (session == null) return false;

            try
            {
                session.SimpleAudioVolume.Mute = muted;
                if (!muted)
                {
                    session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioUtilities] Failed to set session volume: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Safely gets the process ID from a session
        /// </summary>
        /// <param name="session">The session to get the process ID from</param>
        /// <returns>The process ID, or -1 if error</returns>
        public static int GetSessionProcessId(AudioSessionControl session)
        {
            if (session == null) return -1;

            try
            {
                return (int)session.GetProcessID;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Cleans the process name cache to prevent memory leaks
        /// </summary>
        private static void CleanProcessCache()
        {
            try
            {
                if (_processNameCache.Count > MAX_PROCESS_CACHE_SIZE)
                {
                    var keysToRemove = new List<int>();
                    var currentProcessIds = new HashSet<int>();

                    // Get current running process IDs
                    try
                    {
                        var processes = Process.GetProcesses();
                        foreach (var proc in processes)
                        {
                            try
                            {
                                if (proc?.Id > 0)
                                {
                                    currentProcessIds.Add(proc.Id);
                                }
                            }
                            catch { }
                            finally
                            {
                                proc?.Dispose();
                            }
                        }
                    }
                    catch { }

                    // Remove entries for processes that no longer exist
                    foreach (var kvp in _processNameCache)
                    {
                        if (!currentProcessIds.Contains(kvp.Key))
                        {
                            keysToRemove.Add(kvp.Key);
                        }
                    }

                    foreach (var key in keysToRemove)
                    {
                        _processNameCache.Remove(key);
                    }

                    // If still too large, remove oldest entries
                    if (_processNameCache.Count > MAX_PROCESS_CACHE_SIZE)
                    {
                        var excess = _processNameCache.Count - (MAX_PROCESS_CACHE_SIZE / 2);
                        var keysToRemoveList = new List<int>(_processNameCache.Keys);
                        for (int i = 0; i < excess && i < keysToRemoveList.Count; i++)
                        {
                            _processNameCache.Remove(keysToRemoveList[i]);
                        }
                    }

                    Debug.WriteLine($"[AudioUtilities] Process cache cleaned, size: {_processNameCache.Count}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioUtilities] Error during cache cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Force cleanup of all caches (for periodic maintenance)
        /// </summary>
        public static void ForceCleanup()
        {
            lock (_cacheCleanupLock)
            {
                CleanProcessCache();
                _lastProcessCacheCleanup = DateTime.Now;
            }
        }

        /// <summary>
        /// Gets cache statistics for debugging
        /// </summary>
        /// <returns>String with cache information</returns>
        public static string GetCacheStats()
        {
            return $"Process cache: {_processNameCache.Count} entries";
        }
    }
}
