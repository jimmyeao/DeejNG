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
        #region Private Fields

        private const int MAX_PROCESS_CACHE_SIZE = 30;
        private static readonly object _cacheCleanupLock = new object();
        private static readonly Dictionary<int, string> _processNameCache = new();
        private static readonly HashSet<int> _systemProcessIds = new HashSet<int> { 0, 4, 8 };
        private static DateTime _lastProcessCacheCleanup = DateTime.MinValue;

        #endregion Private Fields

        #region Public Methods

        public static void ForceCleanup()
        {
            lock (_cacheCleanupLock)
            {
                CleanProcessCache();
                _lastProcessCacheCleanup = DateTime.Now;
            }
        }

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


        #endregion Public Methods

        #region Private Methods

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

        #endregion Private Methods
    }
}
