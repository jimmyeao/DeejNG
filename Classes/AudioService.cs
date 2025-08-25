using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using DeejNG.Classes;

namespace DeejNG.Services
{
    public class AudioService
    {
        #region Private Fields

        private const int CACHE_REFRESH_SECONDS = 5;
        private const int MAX_SESSION_CACHE_SIZE = 15;
        private readonly object _cacheLock = new object();
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private readonly Dictionary<string, DateTime> _sessionAccessTimes = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, List<SessionInfo>> _sessionCache = new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _systemProcessIds = new HashSet<int> { 0, 4, 8 };
        private HashSet<string> _lastMappedApps = new();
        private DateTime _lastRefresh = DateTime.MinValue;
        private bool _lastUnmappedMuted = false;
        private float _lastUnmappedVolume = -1f;
        private DateTime _lastUnmappedVolumeCall = DateTime.MinValue;

        #endregion Private Fields

        #region Public Constructors

        public AudioService()
        {
            RefreshSessionCache();
        }

        #endregion Public Constructors

        #region Public Methods


               /// <summary>
        /// Applies a mute or unmute state to all audio sessions that are not explicitly listed in the mapped applications.
        /// Skips known system processes and very low process IDs (likely system-related).
        /// </summary>
        /// <param name="isMuted">True to mute, false to unmute.</param>
        /// <param name="mappedApplications">A set of application names that should not be muted by this operation.</param>
        public void ApplyMuteStateToUnmappedApplications(bool isMuted, HashSet<string> mappedApplications)
        {
            try
            {
                // Get the default audio output device (e.g., speakers)
                var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                // Get all current audio sessions associated with that device
                var sessions = device.AudioSessionManager.Sessions;

                int processedCount = 0; // Tracks how many sessions were actually muted/unmuted

                // Loop through each session to identify and modify unmapped applications
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        // Get the process ID of the session and skip system-level processes
                        int processId = (int)session.GetProcessID;
                        if (_systemProcessIds.Contains(processId) || processId < 100) continue;

                        // Get the process name safely
                        string processName = AudioUtilities.GetProcessNameSafely(processId);

                        // Skip if the name is null/empty or it belongs to a mapped app
                        if (string.IsNullOrEmpty(processName) || mappedApplications.Contains(processName)) continue;

                        // Apply mute or unmute to the unmapped session
                        session.SimpleAudioVolume.Mute = isMuted;
                        processedCount++;
                    }
                    catch
                    {
                        // Silently skip any session-specific errors
                    }
                }

                // Log the number of sessions affected
#if DEBUG
                Debug.WriteLine($"[Unmapped] Mute {isMuted} applied to {processedCount} apps");
#endif
            }
            catch (Exception ex)
            {
                // Log unexpected failure during the overall operation
#if DEBUG
                Debug.WriteLine($"[Unmapped] Failed to mute unmapped applications: {ex.GetType().Name}");
#endif
            }
        }


        /// <summary>
        /// Applies volume and mute settings to a specific application's audio session(s),
        /// or to the system master volume if the target is "system" or null/empty.
        /// </summary>
        /// <param name="executable">Executable name (e.g., "chrome", "spotify.exe") or "system" for master volume.</param>
        /// <param name="level">Target volume level (range 0.0f to 1.0f).</param>
        /// <param name="isMuted">Whether to mute the audio (default: false).</param>
        public void ApplyVolumeToTarget(string executable, float level, bool isMuted = false)
        {
            // Ensure the volume is within valid bounds
            level = Math.Clamp(level, 0.0f, 1.0f);

            // If the executable name is empty or refers to "system", adjust the master volume
            if (string.IsNullOrWhiteSpace(executable) ||
                executable.Trim().Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Get the system's default audio output device
                    var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    // Apply mute setting to the entire device
                    device.AudioEndpointVolume.Mute = isMuted;

                    // Set volume only if not muted
                    if (!isMuted)
                    {
                        device.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[AudioService] Failed to set system volume: {ex.Message}");
#endif
                }

                // Early return as system volume has been handled
                return;
            }

            // Normalize the target executable name by stripping extension and converting to lowercase
            string cleanedExecutable = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();

            // Refresh the session cache if it has gone stale
            if ((DateTime.Now - _lastRefresh).TotalSeconds > CACHE_REFRESH_SECONDS)
            {
                RefreshSessionCache();
            }

            try
            {
                // Get all audio sessions from the default output device
                var sessions = _deviceEnumerator
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    .AudioSessionManager.Sessions;

                int sessionCount = 0;

                // Loop through all sessions to find ones matching the executable name
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int processId = (int)session.GetProcessID;

                        // Safely retrieve process name associated with the session
                        string procName = AudioUtilities.GetProcessNameSafely(processId);
                        if (string.IsNullOrEmpty(procName)) continue;

                        // Normalize the process name to match against the target
                        string cleanedProcName = Path.GetFileNameWithoutExtension(procName).ToLowerInvariant();

                        // If the process name matches the target executable, apply settings
                        if (cleanedProcName.Equals(cleanedExecutable, StringComparison.OrdinalIgnoreCase))
                        {
                            session.SimpleAudioVolume.Mute = isMuted;

                            if (!isMuted)
                            {
                                session.SimpleAudioVolume.Volume = level;
                            }

                            sessionCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but continue on individual session processing failures
#if DEBUG
                        Debug.WriteLine($"[AudioService] Error processing session {i}: {ex.Message}");
#endif
                    }
                }

                // Report the result of the volume application
                if (sessionCount == 0)
                {
#if DEBUG
                    Debug.WriteLine($"[AudioService] Could not find any session for {cleanedExecutable}");
#endif
                }
                else
                {
#if DEBUG
                    Debug.WriteLine($"[AudioService] Applied volume to {sessionCount} sessions for {cleanedExecutable}");
#endif
                }
            }
            catch (Exception ex)
            {
                // Catch all outer-level exceptions (e.g., device failure)
#if DEBUG
                Debug.WriteLine($"[AudioService] Failed to apply volume: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// Applies the specified volume and mute settings to all audio sessions that are
        /// not explicitly mapped in the provided list of known application names.
        /// Avoids over-processing by checking for significant changes or rapid re-calls.
        /// </summary>
        /// <param name="level">Volume level (0.0 to 1.0).</param>
        /// <param name="isMuted">Whether the session should be muted.</param>
        /// <param name="mappedApplications">Set of known/mapped application process names to exclude.</param>
        public void ApplyVolumeToUnmappedApplications(float level, bool isMuted, HashSet<string> mappedApplications)
        {
            // Clamp volume level to valid range [0.0, 1.0]
            level = Math.Clamp(level, 0.0f, 1.0f);

            var now = DateTime.Now;

            // Determine if a meaningful change has occurred since last call
            var timeSinceLastCall = (now - _lastUnmappedVolumeCall).TotalMilliseconds;
            var volumeChanged = Math.Abs(_lastUnmappedVolume - level) > 0.05f;
            var muteChanged = _lastUnmappedMuted != isMuted;
            var appsChanged = !_lastMappedApps.SetEquals(mappedApplications);

            // If called too frequently and no actual change in volume/mute/apps, skip processing
            if (timeSinceLastCall < 100 && !volumeChanged && !muteChanged && !appsChanged)
            {
                return;
            }

            // Update internal state tracking
            _lastUnmappedVolumeCall = now;
            _lastUnmappedVolume = level;
            _lastUnmappedMuted = isMuted;
            _lastMappedApps = new HashSet<string>(mappedApplications);

            try
            {
                // Get active audio sessions on the default output device
                var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                int processedCount = 0;

                // Iterate through all available sessions
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        // Skip known system processes and invalid PIDs
                        int processId = (int)session.GetProcessID;
                        if (_systemProcessIds.Contains(processId) || processId < 100) continue;

                        // Resolve the process name safely
                        string processName = AudioUtilities.GetProcessNameSafely(processId);

                        // Skip mapped (known/controlled) applications
                        if (string.IsNullOrEmpty(processName) || mappedApplications.Contains(processName)) continue;

                        // Apply mute/volume only to unmapped apps
                        session.SimpleAudioVolume.Mute = isMuted;
                        if (!isMuted)
                        {
                            session.SimpleAudioVolume.Volume = level;
                        }

                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        // Catch and log errors on a per-session basis to avoid breaking the loop
#if DEBUG
                        Debug.WriteLine($"[Unmapped] Failed to apply volume: {ex.GetType().Name}");
#endif
                    }
                }
            }
            catch (Exception ex)
            {
                // Catch outer exceptions, e.g. device access failures
#if DEBUG
                Debug.WriteLine($"[Unmapped] Failed to apply volume: {ex.GetType().Name}");
#endif
            }
        }


        /// <summary>
        /// Forces cleanup of the session cache by retaining only the most recently accessed session groups
        /// and clearing all others. Also invokes a lower-level cleanup routine.
        /// </summary>
        public void ForceCleanup()
        {
            try
            {
#if DEBUG
                Debug.WriteLine("[AudioService] Force cleanup started");
#endif

                // Ensure thread-safe access to the session cache
                lock (_cacheLock)
                {
                    // Only perform cleanup if there are more than 10 session groups in the cache
                    if (_sessionCache.Count > 10)
                    {
                        // Identify the 5 most recently accessed session groups by last access time
                        var keysToKeep = _sessionCache
                            .OrderByDescending(kvp => _sessionAccessTimes.TryGetValue(kvp.Key, out var time) ? time : DateTime.MinValue)
                            .Take(5)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        // Temporary dictionaries to hold retained session groups and their access times
                        var sessionsToKeep = new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);
                        var accessTimesToKeep = new Dictionary<string, DateTime>();

                        // Populate the temporary dictionaries with the retained entries
                        foreach (var key in keysToKeep)
                        {
                            if (_sessionCache.TryGetValue(key, out var sessionInfos))
                            {
                                sessionsToKeep[key] = sessionInfos;

                                if (_sessionAccessTimes.TryGetValue(key, out var accessTime))
                                {
                                    accessTimesToKeep[key] = accessTime;
                                }
                            }
                        }

                        // Clear the existing cache completely
                        _sessionCache.Clear();
                        _sessionAccessTimes.Clear();

                        // Restore only the most recently accessed entries
                        foreach (var kvp in sessionsToKeep)
                        {
                            _sessionCache[kvp.Key] = kvp.Value;
                        }

                        foreach (var kvp in accessTimesToKeep)
                        {
                            _sessionAccessTimes[kvp.Key] = kvp.Value;
                        }

#if DEBUG
                        Debug.WriteLine($"[AudioService] Session cache reduced to {_sessionCache.Count} apps");
#endif
                    }
                }

                // Call additional cleanup logic, e.g., clearing NAudio internals or releasing COM objects
                AudioUtilities.ForceCleanup();

#if DEBUG
                Debug.WriteLine("[AudioService] Force cleanup completed");
#endif
            }
            catch (Exception ex)
            {
                // Log any unexpected errors that occur during the cleanup
#if DEBUG
                Debug.WriteLine($"[AudioService] Error during force cleanup: {ex.Message}");
#endif
            }
        }


        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Refreshes the audio session cache by retrieving active sessions, grouping them by process,
        /// pruning old cache entries, and removing stale or inactive session groups.
        /// </summary>
        private void RefreshSessionCache()
        {
            try
            {
                // Get all current audio sessions from the default render device (e.g., speakers)
                var sessions = _deviceEnumerator
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    .AudioSessionManager.Sessions;

                var currentTime = DateTime.Now;

                // Tracks active process IDs seen during this refresh
                var currentProcessIds = new HashSet<int>();

                // Temporary dictionary to group session info by process name
                var sessionsByApp = new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);

                // Limit number of sessions processed to 20 for performance reasons
                int maxSessionsToProcess = Math.Min(sessions.Count, 20);

                for (int i = 0; i < maxSessionsToProcess; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        int processId = (int)session.GetProcessID;
                        currentProcessIds.Add(processId);

                        // Safely get the process name for this session
                        string procName = AudioUtilities.GetProcessNameSafely(processId);
                        if (string.IsNullOrEmpty(procName)) continue;

                        // Group sessions under the process name
                        if (!sessionsByApp.ContainsKey(procName))
                        {
                            sessionsByApp[procName] = new List<SessionInfo>();
                        }

                        // Add session info to the grouped list
                        sessionsByApp[procName].Add(new SessionInfo
                        {
                            Session = session,
                            ProcessName = procName,
                            ProcessId = processId,
                            LastSeen = currentTime
                        });
                    }
                    catch (Exception ex)
                    {
                        // Catch and log any issues with individual session processing
#if DEBUG
                        Debug.WriteLine($"[AudioService] Error refreshing session {i}: {ex.Message}");
#endif
                    }
                }

                // Lock the cache for thread-safe update
                lock (_cacheLock)
                {
                    // If cache is too large, evict the oldest half of the entries
                    if (_sessionCache.Count >= MAX_SESSION_CACHE_SIZE)
                    {
                        var oldestKeys = _sessionCache
                            .OrderBy(kvp => _sessionAccessTimes.TryGetValue(kvp.Key, out var time) ? time : DateTime.MinValue)
                            .Take(_sessionCache.Count - (MAX_SESSION_CACHE_SIZE / 2))
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var key in oldestKeys)
                        {
                            _sessionCache.Remove(key);
                            _sessionAccessTimes.Remove(key);
                        }
                    }

                    // Add or update session groups in the cache
                    foreach (var kvp in sessionsByApp)
                    {
                        _sessionCache[kvp.Key] = kvp.Value;

                        // Only set access time if this app group was not previously tracked
                        if (!_sessionAccessTimes.ContainsKey(kvp.Key))
                        {
                            _sessionAccessTimes[kvp.Key] = currentTime;
                        }
                    }

                    // Determine which cached session groups are stale or no longer active
                    var staleThreshold = currentTime.AddMinutes(-2);
                    var toRemove = new List<string>();

                    foreach (var kvp in _sessionCache)
                    {
                        // Check if last seen is too old
                        bool isStale = _sessionAccessTimes.TryGetValue(kvp.Key, out var lastAccess)
                                       && lastAccess < staleThreshold;

                        // Check if none of the sessions in this group have a live process
                        bool hasDeadProcess = kvp.Value.All(si => !currentProcessIds.Contains(si.ProcessId));

                        if (isStale || hasDeadProcess)
                        {
                            toRemove.Add(kvp.Key);
                        }
                    }

                    // Remove stale/inactive session groups from cache
                    foreach (var key in toRemove)
                    {
                        _sessionCache.Remove(key);
                        _sessionAccessTimes.Remove(key);
                    }

                    if (toRemove.Count > 0)
                    {
#if DEBUG
                        Debug.WriteLine($"[AudioService] Removed {toRemove.Count} stale session groups from cache");
#endif
                    }
                }

                // Update timestamp of the last refresh
                _lastRefresh = currentTime;

                // Log final stats from the refresh
#if DEBUG
                Debug.WriteLine($"[AudioService] Cache refreshed: {sessionsByApp.Sum(kvp => kvp.Value.Count)} sessions in {sessionsByApp.Count} apps");
#endif
            }
            catch (Exception ex)
            {
                // Catch-all for unexpected errors in the refresh process
#if DEBUG
                Debug.WriteLine($"[AudioService] Failed to refresh session cache: {ex.Message}");
#endif
            }
        }


        #endregion Private Methods

        #region Private Classes

        private class SessionInfo
        {
            #region Public Properties

            public DateTime LastSeen { get; set; }
            public int ProcessId { get; set; }
            public string ProcessName { get; set; }
            public AudioSessionControl Session { get; set; }

            #endregion Public Properties
        }

        #endregion Private Classes
    }
}
