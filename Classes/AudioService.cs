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
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, DateTime> _sessionAccessTimes = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, SessionInfo> _sessionCache = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastRefresh = DateTime.MinValue;
        private const int CACHE_REFRESH_SECONDS = 5; // Refresh cache every 5 seconds
        private DateTime _lastUnmappedVolumeCall = DateTime.MinValue;
        private float _lastUnmappedVolume = -1f;
        private bool _lastUnmappedMuted = false;
        private HashSet<string> _lastMappedApps = new();
        private readonly HashSet<int> _systemProcessIds = new HashSet<int> { 0, 4, 8 }; // Known system process IDs
        private const int MAX_SESSION_CACHE_SIZE = 15; // Reduced from unlimited
 

        private class SessionInfo
        {
            public AudioSessionControl Session { get; set; }
            public string ProcessName { get; set; }
            public int ProcessId { get; set; }
            public DateTime LastSeen { get; set; }
        }

        public AudioService()
        {
            RefreshSessionCache();
        }

        public void ApplyMuteStateToTarget(string target, bool isMuted)
        {
            if (string.IsNullOrWhiteSpace(target)) return;

            if (target.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                var dev = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                dev.AudioEndpointVolume.Mute = isMuted;
                return;
            }

            // See if we need to refresh the cache
            if ((DateTime.Now - _lastRefresh).TotalSeconds > CACHE_REFRESH_SECONDS)
            {
                RefreshSessionCache();
            }

            // Try to find the session in our cache
            lock (_cacheLock)
            {
                if (_sessionCache.TryGetValue(target, out var sessionInfo))
                {
                    try
                    {
                        sessionInfo.Session.SimpleAudioVolume.Mute = isMuted;
                        _sessionAccessTimes[target] = DateTime.Now;
                        return;
                    }
                    catch (Exception ex)
                    {
                        // If we can't access the session, it may have been closed
                        Debug.WriteLine($"[AudioService] Failed to mute {target}: {ex.Message}");
                        _sessionCache.Remove(target);
                    }
                }
            }

            // If we didn't find it in the cache or had an error, try direct approach
            try
            {
                var dev = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = dev.AudioSessionManager.Sessions;

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];

                        // Skip if null
                        if (session == null) continue;

                        string sessionId = session.GetSessionIdentifier?.ToLower() ?? "";
                        string instanceId = session.GetSessionInstanceIdentifier?.ToLower() ?? "";
                        int processId = 0;

                        try { processId = (int)session.GetProcessID; }
                        catch { continue; } // Skip if we can't get process ID

                        // Try to get the process name
                        string processName = AudioUtilities.GetProcessNameSafely(processId);
                        
                        if (string.IsNullOrEmpty(processName))
                        {
                            // If we can't get the name, use the ID in the session ID
                            processName = $"unknown_{processId}";
                        }

                        // Check if this session matches our target
                        if (processName.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                            sessionId.Contains(target) ||
                            instanceId.Contains(target))
                        {
                            session.SimpleAudioVolume.Mute = isMuted;

                            // Add to cache
                            lock (_cacheLock)
                            {
                                _sessionCache[target] = new SessionInfo
                                {
                                    Session = session,
                                    ProcessName = processName,
                                    ProcessId = processId,
                                    LastSeen = DateTime.Now
                                };
                                _sessionAccessTimes[target] = DateTime.Now;
                            }

                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioService] Error processing session {i}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] Failed to apply mute state: {ex.Message}");
            }
        }

        public void ApplyMuteStateToMultipleTargets(List<string> targets, bool isMuted)
        {
            if (targets == null || !targets.Any()) return;

            foreach (var target in targets)
            {
                ApplyMuteStateToTarget(target, isMuted);
            }
        }

        public void ApplyVolumeToTarget(string executable, float level, bool isMuted = false)
        {
            level = Math.Clamp(level, 0.0f, 1.0f);

            if (string.IsNullOrWhiteSpace(executable) ||
                executable.Trim().Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    device.AudioEndpointVolume.Mute = isMuted;

                    if (!isMuted)
                    {
                        device.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioService] Failed to set system volume: {ex.Message}");
                }
                return;
            }

            // Normalize the executable name
            string cleanedExecutable = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
            bool sessionFound = false;

            // Iterate through all active sessions to find every match
            try
            {
                var sessions = _deviceEnumerator
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    .AudioSessionManager.Sessions;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        int processId = (int)session.GetProcessID;
                        string procName = AudioUtilities.GetProcessNameSafely(processId);

                        if (string.IsNullOrEmpty(procName))
                        {
                            continue;
                        }

                        string cleanedProcName = Path.GetFileNameWithoutExtension(procName).ToLowerInvariant();

                        if (cleanedProcName.Equals(cleanedExecutable, StringComparison.OrdinalIgnoreCase))
                        {
                            session.SimpleAudioVolume.Mute = isMuted;

                            if (!isMuted)
                            {
                                session.SimpleAudioVolume.Volume = level;
                            }
                            sessionFound = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioService] Error processing session {i}: {ex.Message}");
                    }
                }

                if (!sessionFound)
                {
                    Debug.WriteLine($"[AudioService] Could not find any session for {cleanedExecutable}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] Failed to apply volume: {ex.Message}");
            }
        }
        public void ApplyVolumeToUnmappedApplications(float level, bool isMuted, HashSet<string> mappedApplications)
        {
            level = Math.Clamp(level, 0.0f, 1.0f);

            // THROTTLING: Only process if enough time has passed or values changed significantly
            var now = DateTime.Now;
            var timeSinceLastCall = (now - _lastUnmappedVolumeCall).TotalMilliseconds;
            var volumeChanged = Math.Abs(_lastUnmappedVolume - level) > 0.05f; // 2% change threshold (more sensitive)
            var muteChanged = _lastUnmappedMuted != isMuted;
            var appsChanged = !_lastMappedApps.SetEquals(mappedApplications);

            if (timeSinceLastCall < 100 && !volumeChanged && !muteChanged && !appsChanged) // 250ms throttle
            {
                return; // Skip this call to reduce processing
            }

            _lastUnmappedVolumeCall = now;
            _lastUnmappedVolume = level;
            _lastUnmappedMuted = isMuted;
            _lastMappedApps = new HashSet<string>(mappedApplications);

            try
            {
                var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                int processedCount = 0;
                int skippedCount = 0;

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null)
                        {
                            skippedCount++;
                            continue;
                        }

                        // Get process ID first
                        int processId;
                        try
                        {
                            processId = (int)session.GetProcessID;
                        }
                        catch
                        {
                            skippedCount++;
                            continue;
                        }

                        // Skip system sessions immediately - these can't be controlled and cause Win32Exception
                        if (_systemProcessIds.Contains(processId) || processId < 100)
                        {
                            skippedCount++;
                            continue;
                        }

                        // Get process name with caching
                        string processName = AudioUtilities.GetProcessNameSafely(processId);

                        if (string.IsNullOrEmpty(processName))
                        {
                            skippedCount++;
                            continue;
                        }

                        // Skip if this application is already mapped to a slider
                        if (mappedApplications.Contains(processName))
                        {
                            skippedCount++;
                            continue;
                        }

                        // Apply volume to this unmapped session
                        try
                        {
                            session.SimpleAudioVolume.Mute = isMuted;
                            if (!isMuted)
                            {
                                session.SimpleAudioVolume.Volume = level;
                            }
                            processedCount++;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Unmapped] Failed to apply volume to {processName}: {ex.GetType().Name}");
                            skippedCount++;
                        }
                    }
                    catch
                    {
                        skippedCount++;
                    }
                }

                // Only log significant changes or errors
              
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Unmapped] Failed to apply volume: {ex.GetType().Name}");
            }
        }
        // Replace your ApplyMuteStateToUnmappedApplications method in AudioService.cs with this safer version:

        public void ApplyMuteStateToUnmappedApplications(bool isMuted, HashSet<string> mappedApplications)
        {
            try
            {
                var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                int processedCount = 0;
                int skippedCount = 0;

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null)
                        {
                            skippedCount++;
                            continue;
                        }

                        int processId;
                        try
                        {
                            processId = (int)session.GetProcessID;
                        }
                        catch
                        {
                            skippedCount++;
                            continue;
                        }

                        // Skip system sessions that cause Win32Exception
                        if (_systemProcessIds.Contains(processId) || processId < 100)
                        {
                            skippedCount++;
                            continue;
                        }

                        string processName = AudioUtilities.GetProcessNameSafely(processId);

                        if (string.IsNullOrEmpty(processName))
                        {
                            skippedCount++;
                            continue;
                        }

                        // Skip if this application is already mapped
                        if (mappedApplications.Contains(processName))
                        {
                            skippedCount++;
                            continue;
                        }

                        try
                        {
                            session.SimpleAudioVolume.Mute = isMuted;
                            processedCount++;
                        }
                        catch
                        {
                            skippedCount++;
                        }
                    }
                    catch
                    {
                        skippedCount++;
                    }
                }

                Debug.WriteLine($"[Unmapped] Mute {isMuted} applied to {processedCount} apps");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Unmapped] Failed to mute unmapped applications: {ex.GetType().Name}");
            }
        }
        private void RefreshSessionCache()
        {
            try
            {
                var sessions = _deviceEnumerator
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    .AudioSessionManager.Sessions;

                var currentTime = DateTime.Now;
                var processedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var currentProcessIds = new HashSet<int>();

                // Limit the number of sessions we process
                int maxSessionsToProcess = Math.Min(sessions.Count, 20); // Don't process more than 20 sessions

                for (int i = 0; i < maxSessionsToProcess; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        int processId = (int)session.GetProcessID;
                        currentProcessIds.Add(processId);

                        string procName = AudioUtilities.GetProcessNameSafely(processId);

                        if (string.IsNullOrEmpty(procName))
                        {
                            continue;
                        }

                        processedSessions.Add(procName);

                        lock (_cacheLock)
                        {
                            // Limit session cache size
                            if (_sessionCache.Count >= MAX_SESSION_CACHE_SIZE)
                            {
                                // Remove oldest entries
                                var oldestKey = _sessionCache
                                    .OrderBy(kvp => kvp.Value.LastSeen)
                                    .First().Key;
                                _sessionCache.Remove(oldestKey);
                                _sessionAccessTimes.Remove(oldestKey);
                            }

                            // CRITICAL FIX: Check if we have a cached session for this process name
                            // but with a different PID (indicating app restart)
                            if (_sessionCache.TryGetValue(procName, out var existing))
                            {
                                if (existing.ProcessId != processId)
                                {
                                    // Process restarted with new PID - replace the cached session
                                    Debug.WriteLine($"[AudioService] Process {procName} restarted: PID {existing.ProcessId} -> {processId}");
                                    _sessionCache[procName] = new SessionInfo
                                    {
                                        Session = session,
                                        ProcessName = procName,
                                        ProcessId = processId,
                                        LastSeen = currentTime
                                    };
                                }
                                else
                                {
                                    existing.LastSeen = currentTime;
                                }
                            }
                            else
                            {
                                _sessionCache[procName] = new SessionInfo
                                {
                                    Session = session,
                                    ProcessName = procName,
                                    ProcessId = processId,
                                    LastSeen = currentTime
                                };
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioService] Error refreshing session {i}: {ex.Message}");
                    }
                }

                // Remove stale entries more aggressively and invalidate sessions for dead processes
                lock (_cacheLock)
                {
                    var staleThreshold = currentTime.AddMinutes(-2); // Reduce from default to 2 minutes
                    var toRemove = new List<string>();

                    foreach (var kvp in _sessionCache)
                    {
                        // Remove if too old OR if the process ID is no longer active
                        if (kvp.Value.LastSeen < staleThreshold || !currentProcessIds.Contains(kvp.Value.ProcessId))
                        {
                            toRemove.Add(kvp.Key);
                        }
                    }

                    foreach (var key in toRemove)
                    {
                        _sessionCache.Remove(key);
                        _sessionAccessTimes.Remove(key);
                    }

                    if (toRemove.Count > 0)
                    {
                        Debug.WriteLine($"[AudioService] Removed {toRemove.Count} stale sessions from cache");
                    }
                }

                _lastRefresh = currentTime;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] Failed to refresh session cache: {ex.Message}");
            }
        }
        public void ForceCleanup()
        {
            try
            {
                Debug.WriteLine("[AudioService] Force cleanup started");

                lock (_cacheLock)
                {
                    // Clear session cache if it's too large
                    if (_sessionCache.Count > 10)
                    {
                        var keysToKeep = _sessionCache
                            .OrderByDescending(kvp => kvp.Value.LastSeen)
                            .Take(5)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        // Create temporary dictionaries to hold the data we want to keep
                        var sessionsToKeep = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);
                        var accessTimesToKeep = new Dictionary<string, DateTime>();

                        foreach (var key in keysToKeep)
                        {
                            if (_sessionCache.TryGetValue(key, out var sessionInfo))
                            {
                                sessionsToKeep[key] = sessionInfo;
                                if (_sessionAccessTimes.TryGetValue(key, out var accessTime))
                                {
                                    accessTimesToKeep[key] = accessTime;
                                }
                            }
                        }

                        // Clear the readonly dictionaries and repopulate them
                        _sessionCache.Clear();
                        _sessionAccessTimes.Clear();

                        foreach (var kvp in sessionsToKeep)
                        {
                            _sessionCache[kvp.Key] = kvp.Value;
                        }

                        foreach (var kvp in accessTimesToKeep)
                        {
                            _sessionAccessTimes[kvp.Key] = kvp.Value;
                        }

                        Debug.WriteLine($"[AudioService] Session cache reduced to {_sessionCache.Count} entries");
                    }
                }

                // Force process cache cleanup via centralized utility
                AudioUtilities.ForceCleanup();

                Debug.WriteLine("[AudioService] Force cleanup completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] Error during force cleanup: {ex.Message}");
            }
        }
    }
}