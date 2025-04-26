using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;

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
                        string processName = "";
                        try
                        {
                            var process = Process.GetProcessById(processId);
                            processName = Path.GetFileNameWithoutExtension(process.MainModule.FileName)
                                         .ToLowerInvariant();
                        }
                        catch
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

            // See if we need to refresh the cache
            if ((DateTime.Now - _lastRefresh).TotalSeconds > CACHE_REFRESH_SECONDS)
            {
                RefreshSessionCache();
            }

            // Try to find the session in our cache
            lock (_cacheLock)
            {
                if (_sessionCache.TryGetValue(cleanedExecutable, out var sessionInfo))
                {
                    try
                    {
                        sessionInfo.Session.SimpleAudioVolume.Mute = isMuted;

                        if (!isMuted)
                        {
                            sessionInfo.Session.SimpleAudioVolume.Volume = level;
                        }

                        _sessionAccessTimes[cleanedExecutable] = DateTime.Now;
                        return;
                    }
                    catch (Exception ex)
                    {
                        // If we can't access the session, it may have been closed
                        Debug.WriteLine($"[AudioService] Failed to set volume for {cleanedExecutable}: {ex.Message}");
                        _sessionCache.Remove(cleanedExecutable);
                    }
                }
            }

            // If we didn't find it in the cache or had an error, try direct approach
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
                        string procName = "";

                        try
                        {
                            procName = Process.GetProcessById(processId).ProcessName.ToLowerInvariant();
                        }
                        catch
                        {
                            continue; // Skip if we can't get the process name
                        }

                        string cleanedProcName = Path.GetFileNameWithoutExtension(procName).ToLowerInvariant();

                        if (cleanedProcName.Equals(cleanedExecutable, StringComparison.OrdinalIgnoreCase))
                        {
                            session.SimpleAudioVolume.Mute = isMuted;

                            if (!isMuted)
                            {
                                session.SimpleAudioVolume.Volume = level;
                            }

                            // Add to cache
                            lock (_cacheLock)
                            {
                                _sessionCache[cleanedExecutable] = new SessionInfo
                                {
                                    Session = session,
                                    ProcessName = cleanedProcName,
                                    ProcessId = processId,
                                    LastSeen = DateTime.Now
                                };
                                _sessionAccessTimes[cleanedExecutable] = DateTime.Now;
                            }

                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioService] Error processing session {i}: {ex.Message}");
                    }
                }

                // If we get here, we didn't find the session - log it
                Debug.WriteLine($"[AudioService] Could not find session for {cleanedExecutable}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] Failed to apply volume: {ex.Message}");
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

                // Process all current sessions
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        int processId = (int)session.GetProcessID;

                        string procName = "";
                        try
                        {
                            var proc = Process.GetProcessById(processId);
                            procName = Path.GetFileNameWithoutExtension(proc.MainModule.FileName)
                                     .ToLowerInvariant();
                        }
                        catch
                        {
                            continue; // Skip if we can't get process info
                        }

                        if (string.IsNullOrEmpty(procName)) continue;

                        processedSessions.Add(procName);

                        lock (_cacheLock)
                        {
                            // Update or add to cache
                            if (_sessionCache.TryGetValue(procName, out var existing))
                            {
                                existing.LastSeen = currentTime;
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

                // Remove any sessions from cache that weren't seen
                lock (_cacheLock)
                {
                    var toRemove = _sessionCache.Keys
                        .Where(key => !processedSessions.Contains(key))
                        .ToList();

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
    }
}