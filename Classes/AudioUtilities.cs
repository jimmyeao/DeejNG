using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DeejNG.Classes
{
    /// <summary>
    /// Centralized utility class for audio-related operations to prevent code duplication.
    /// Includes safe process name resolution and process cache cleanup functionality.
    /// </summary>
    public static class AudioUtilities
    {
        #region Private Fields

        // Maximum number of entries allowed in the process name cache
        private const int MAX_PROCESS_CACHE_SIZE = 30;

        // Lock to ensure thread-safe access to the cache
        private static readonly object _cacheCleanupLock = new object();

        // Cache for mapping process IDs to their names to avoid repeated expensive lookups
        private static readonly Dictionary<int, string> _processNameCache = new();

        // List of system process IDs to skip (e.g., Idle, System)
        private static readonly HashSet<int> _systemProcessIds = new HashSet<int> { 0, 4, 8 };

        // Timestamp of the last time the process cache was cleaned
        private static DateTime _lastProcessCacheCleanup = DateTime.MinValue;


        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        #endregion Private Fields

        #region Public Methods

        /// <summary>
        /// Forces an immediate cleanup of the internal process name cache.
        /// Can be called manually when a known large number of processes has terminated.
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
        /// Retrieves the process name for a given process ID safely.
        /// Uses a cache to avoid repeated lookups and avoids access to MainModule (which can throw).
        /// Returns an empty string on failure or for system processes.
        /// </summary>
        public static string GetProcessNameSafely(int processId)
        {
            // Clean cache every 60 seconds to remove stale entries
            lock (_cacheCleanupLock)
            {
                if ((DateTime.Now - _lastProcessCacheCleanup).TotalSeconds > 60)
                {
                    CleanProcessCache();
                    _lastProcessCacheCleanup = DateTime.Now;
                }
            }

            // Return from cache if available
            if (_processNameCache.TryGetValue(processId, out string cachedName))
            {
                return cachedName;
            }

            // Avoid known system processes and invalid PIDs
            if (_systemProcessIds.Contains(processId) || processId < 100)
            {
                _processNameCache[processId] = "";
                return "";
            }

            string processName = "";

            try
            {
                // Get the process name safely without accessing MainModule
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
                // Ignore any exception and treat as unknown process
                processName = "";
            }

            // Cache the result (even empty to avoid repeated failures)
            _processNameCache[processId] = processName;
            return processName;
        }
        
        public static string GetCurrentFocusTarget()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return "";
            }

            GetWindowThreadProcessId(hWnd, out uint processId);
            return GetProcessNameSafely((int)processId);
        }
        
        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Cleans the process name cache by removing entries for terminated processes.
        /// Also trims the cache size if it grows too large.
        /// </summary>
        private static void CleanProcessCache()
        {
            try
            {
                // Only clean if the cache has exceeded the max size
                if (_processNameCache.Count > MAX_PROCESS_CACHE_SIZE)
                {
                    var keysToRemove = new List<int>();
                    var currentProcessIds = new HashSet<int>();

                    // Get list of currently running process IDs
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
                                proc?.Dispose(); // Always dispose to avoid leaks
                            }
                        }
                    }
                    catch { } // Fail-safe: ignore failure in getting running processes

                    // Identify cache entries for processes that no longer exist
                    foreach (var kvp in _processNameCache)
                    {
                        if (!currentProcessIds.Contains(kvp.Key))
                        {
                            keysToRemove.Add(kvp.Key);
                        }
                    }

                    // Remove stale entries
                    foreach (var key in keysToRemove)
                    {
                        _processNameCache.Remove(key);
                    }

                    // If still too big, trim oldest entries arbitrarily (first N keys)
                    if (_processNameCache.Count > MAX_PROCESS_CACHE_SIZE)
                    {
                        var excess = _processNameCache.Count - (MAX_PROCESS_CACHE_SIZE / 2);
                        var keysToRemoveList = new List<int>(_processNameCache.Keys);

                        for (int i = 0; i < excess && i < keysToRemoveList.Count; i++)
                        {
                            _processNameCache.Remove(keysToRemoveList[i]);
                        }
                    }

#if DEBUG
                    Debug.WriteLine($"[AudioUtilities] Process cache cleaned, size: {_processNameCache.Count}");
#endif
                }
            }
            catch (Exception ex)
            {
                // Log any unexpected cleanup errors
#if DEBUG
                Debug.WriteLine($"[AudioUtilities] Error during cache cleanup: {ex.Message}");
#endif
            }
        }

        #endregion Private Methods
    }
}
