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

        // 🔥 KEY CHANGE: Cache multiple sessions per app instead of single session
        private readonly Dictionary<string, List<SessionInfo>> _sessionCache = new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastRefresh = DateTime.MinValue;
        private const int CACHE_REFRESH_SECONDS = 5;
        private DateTime _lastUnmappedVolumeCall = DateTime.MinValue;
        private float _lastUnmappedVolume = -1f;
        private bool _lastUnmappedMuted = false;
        private HashSet<string> _lastMappedApps = new();
        private readonly HashSet<int> _systemProcessIds = new HashSet<int> { 0, 4, 8 };
        private const int MAX_SESSION_CACHE_SIZE = 15;

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

            string cleanedExecutable = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();

            // Use cache if fresh, otherwise enumerate directly (like PR does)
            if ((DateTime.Now - _lastRefresh).TotalSeconds > CACHE_REFRESH_SECONDS)
            {
                RefreshSessionCache();
            }

            // Always enumerate all sessions to find ALL matches (PR behavior)
            // But use cached process names for performance
            try
            {
                var sessions = _deviceEnumerator
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    .AudioSessionManager.Sessions;

                int sessionCount = 0;

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int processId = (int)session.GetProcessID;
                        string procName = AudioUtilities.GetProcessNameSafely(processId);

                        if (string.IsNullOrEmpty(procName)) continue;

                        string cleanedProcName = Path.GetFileNameWithoutExtension(procName).ToLowerInvariant();

                        if (cleanedProcName.Equals(cleanedExecutable, StringComparison.OrdinalIgnoreCase))
                        {
                            session.SimpleAudioVolume.Mute = isMuted;
                            if (!isMuted)
                            {
                                session.SimpleAudioVolume.Volume = level;
                            }
                            sessionCount++;
                            // DON'T BREAK - continue to find more sessions (PR's key change)
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioService] Error processing session {i}: {ex.Message}");
                    }
                }

                if (sessionCount == 0)
                {
                    Debug.WriteLine($"[AudioService] Could not find any session for {cleanedExecutable}");
                }
                else
                {
                    Debug.WriteLine($"[AudioService] Applied volume to {sessionCount} sessions for {cleanedExecutable}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] Failed to apply volume: {ex.Message}");
            }
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

            // 🚀 SAME HYBRID APPROACH for muting
            bool sessionFound = false;

            // Try cache first
            if ((DateTime.Now - _lastRefresh).TotalSeconds <= CACHE_REFRESH_SECONDS)
            {
                lock (_cacheLock)
                {
                    if (_sessionCache.TryGetValue(target, out var cachedSessions))
                    {
                        var validSessions = new List<SessionInfo>();

                        foreach (var sessionInfo in cachedSessions)
                        {
                            try
                            {
                                sessionInfo.Session.SimpleAudioVolume.Mute = isMuted;
                                validSessions.Add(sessionInfo);
                                sessionFound = true;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[AudioService] Failed to mute cached session for {target}: {ex.Message}");
                            }
                        }

                        if (validSessions.Count > 0)
                        {
                            _sessionCache[target] = validSessions;
                            _sessionAccessTimes[target] = DateTime.Now;
                        }
                        else
                        {
                            _sessionCache.Remove(target);
                            _sessionAccessTimes.Remove(target);
                        }

                        if (sessionFound) return;
                    }
                }
            }

            // Fallback to direct enumeration if cache miss or invalid
            try
            {
                var dev = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = dev.AudioSessionManager.Sessions;
                var foundSessions = new List<SessionInfo>();

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int processId = (int)session.GetProcessID;
                        string processName = AudioUtilities.GetProcessNameSafely(processId);

                        if (string.IsNullOrEmpty(processName))
                        {
                            processName = $"unknown_{processId}";
                        }

                        if (processName.Equals(target, StringComparison.OrdinalIgnoreCase))
                        {
                            session.SimpleAudioVolume.Mute = isMuted;
                            foundSessions.Add(new SessionInfo
                            {
                                Session = session,
                                ProcessName = processName,
                                ProcessId = processId,
                                LastSeen = DateTime.Now
                            });
                            sessionFound = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioService] Error processing session {i}: {ex.Message}");
                    }
                }

                // Update cache with found sessions
                if (foundSessions.Count > 0)
                {
                    lock (_cacheLock)
                    {
                        _sessionCache[target] = foundSessions;
                        _sessionAccessTimes[target] = DateTime.Now;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] Failed to apply mute state: {ex.Message}");
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
                var currentProcessIds = new HashSet<int>();

                // 🔥 NEW: Group sessions by process name to handle multiple sessions per app
                var sessionsByApp = new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);

                int maxSessionsToProcess = Math.Min(sessions.Count, 20);

                for (int i = 0; i < maxSessionsToProcess; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        int processId = (int)session.GetProcessID;
                        currentProcessIds.Add(processId);

                        string procName = AudioUtilities.GetProcessNameSafely(processId);
                        if (string.IsNullOrEmpty(procName)) continue;

                        // Group sessions by app name
                        if (!sessionsByApp.ContainsKey(procName))
                        {
                            sessionsByApp[procName] = new List<SessionInfo>();
                        }

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
                        Debug.WriteLine($"[AudioService] Error refreshing session {i}: {ex.Message}");
                    }
                }

                lock (_cacheLock)
                {
                    // Clean up cache size before adding new entries
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

                    // Update cache with grouped sessions
                    foreach (var kvp in sessionsByApp)
                    {
                        _sessionCache[kvp.Key] = kvp.Value;
                        if (!_sessionAccessTimes.ContainsKey(kvp.Key))
                        {
                            _sessionAccessTimes[kvp.Key] = currentTime;
                        }
                    }

                    // Remove stale entries
                    var staleThreshold = currentTime.AddMinutes(-2);
                    var toRemove = new List<string>();

                    foreach (var kvp in _sessionCache)
                    {
                        bool isStale = _sessionAccessTimes.TryGetValue(kvp.Key, out var lastAccess) && lastAccess < staleThreshold;
                        bool hasDeadProcess = kvp.Value.All(si => !currentProcessIds.Contains(si.ProcessId));

                        if (isStale || hasDeadProcess)
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
                        Debug.WriteLine($"[AudioService] Removed {toRemove.Count} stale session groups from cache");
                    }
                }

                _lastRefresh = currentTime;
                Debug.WriteLine($"[AudioService] Cache refreshed: {sessionsByApp.Sum(kvp => kvp.Value.Count)} sessions in {sessionsByApp.Count} apps");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] Failed to refresh session cache: {ex.Message}");
            }
        }

        // Keep all other methods unchanged...
        public void ApplyMuteStateToMultipleTargets(List<string> targets, bool isMuted)
        {
            if (targets == null || !targets.Any()) return;
            foreach (var target in targets)
            {
                ApplyMuteStateToTarget(target, isMuted);
            }
        }

        public void ApplyVolumeToUnmappedApplications(float level, bool isMuted, HashSet<string> mappedApplications)
        {
            level = Math.Clamp(level, 0.0f, 1.0f);

            var now = DateTime.Now;
            var timeSinceLastCall = (now - _lastUnmappedVolumeCall).TotalMilliseconds;
            var volumeChanged = Math.Abs(_lastUnmappedVolume - level) > 0.05f;
            var muteChanged = _lastUnmappedMuted != isMuted;
            var appsChanged = !_lastMappedApps.SetEquals(mappedApplications);

            if (timeSinceLastCall < 100 && !volumeChanged && !muteChanged && !appsChanged)
            {
                return;
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

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int processId = (int)session.GetProcessID;
                        if (_systemProcessIds.Contains(processId) || processId < 100) continue;

                        string processName = AudioUtilities.GetProcessNameSafely(processId);
                        if (string.IsNullOrEmpty(processName) || mappedApplications.Contains(processName)) continue;

                        session.SimpleAudioVolume.Mute = isMuted;
                        if (!isMuted)
                        {
                            session.SimpleAudioVolume.Volume = level;
                        }
                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Unmapped] Failed to apply volume: {ex.GetType().Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Unmapped] Failed to apply volume: {ex.GetType().Name}");
            }
        }

        public void ApplyMuteStateToUnmappedApplications(bool isMuted, HashSet<string> mappedApplications)
        {
            try
            {
                var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                int processedCount = 0;

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session == null) continue;

                        int processId = (int)session.GetProcessID;
                        if (_systemProcessIds.Contains(processId) || processId < 100) continue;

                        string processName = AudioUtilities.GetProcessNameSafely(processId);
                        if (string.IsNullOrEmpty(processName) || mappedApplications.Contains(processName)) continue;

                        session.SimpleAudioVolume.Mute = isMuted;
                        processedCount++;
                    }
                    catch { }
                }

                Debug.WriteLine($"[Unmapped] Mute {isMuted} applied to {processedCount} apps");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Unmapped] Failed to mute unmapped applications: {ex.GetType().Name}");
            }
        }

        public void ForceCleanup()
        {
            try
            {
                Debug.WriteLine("[AudioService] Force cleanup started");

                lock (_cacheLock)
                {
                    if (_sessionCache.Count > 10)
                    {
                        var keysToKeep = _sessionCache
                            .OrderByDescending(kvp => _sessionAccessTimes.TryGetValue(kvp.Key, out var time) ? time : DateTime.MinValue)
                            .Take(5)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        var sessionsToKeep = new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);
                        var accessTimesToKeep = new Dictionary<string, DateTime>();

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

                        Debug.WriteLine($"[AudioService] Session cache reduced to {_sessionCache.Count} apps");
                    }
                }

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