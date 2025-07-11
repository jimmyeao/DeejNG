using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DeejNG.Dialogs;
using DeejNG.Models;

namespace DeejNG.Classes
{
    /// <summary>
    /// Centralized manager for audio session events that decouples handlers from specific controls
    /// </summary>
    public class AudioSessionEventManager
    {
        private static AudioSessionEventManager _instance;
        private static readonly object _lock = new object();

        // Thread-safe dictionaries for managing app-to-control mappings
        private readonly ConcurrentDictionary<string, ChannelControl> _appToControlMap = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _controlToAppsMap = new();
        private readonly object _mappingLock = new object();

        private AudioSessionEventManager() { }

        public static AudioSessionEventManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new AudioSessionEventManager();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Updates the mapping when a control's targets change
        /// </summary>
        /// <param name="control">The control whose targets changed</param>
        /// <param name="newTargets">The new list of targets for this control</param>
        public void UpdateControlMapping(ChannelControl control, List<AudioTarget> newTargets)
        {
            if (control == null) return;

            lock (_mappingLock)
            {
                // Remove this control from all previous mappings
                ClearControlFromMappings(control);

                // Add new mappings for this control
                var appNames = new HashSet<string>();
                
                foreach (var target in newTargets ?? new List<AudioTarget>())
                {
                    if (!target.IsInputDevice && !string.IsNullOrWhiteSpace(target.Name))
                    {
                        string normalizedName = target.Name.ToLowerInvariant();
                        _appToControlMap[normalizedName] = control;
                        appNames.Add(normalizedName);
                    }
                }

                _controlToAppsMap[GetControlKey(control)] = appNames;

                Debug.WriteLine($"[EventManager] Updated mapping for control - {appNames.Count} apps assigned");
            }
        }

        /// <summary>
        /// Removes a control from all mappings (used when control is disposed)
        /// </summary>
        /// <param name="control">The control to remove</param>
        public void RemoveControl(ChannelControl control)
        {
            if (control == null) return;

            lock (_mappingLock)
            {
                ClearControlFromMappings(control);
                Debug.WriteLine($"[EventManager] Removed control from all mappings");
            }
        }

        /// <summary>
        /// Handles volume change events from audio sessions
        /// </summary>
        /// <param name="processName">The name of the process whose volume changed</param>
        /// <param name="isMuted">Whether the session is muted</param>
        public void OnSessionVolumeChanged(string processName, bool isMuted)
        {
            if (string.IsNullOrWhiteSpace(processName)) return;

            string normalizedName = processName.ToLowerInvariant();

            if (_appToControlMap.TryGetValue(normalizedName, out var control) && control != null)
            {
                try
                {
                    // Ensure we're on the UI thread
                    if (Application.Current?.Dispatcher?.CheckAccess() == false)
                    {
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            SafeSetMuted(control, isMuted, normalizedName);
                        });
                    }
                    else
                    {
                        SafeSetMuted(control, isMuted, normalizedName);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[EventManager] Error updating control for {normalizedName}: {ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine($"[EventManager] No control found for {normalizedName}");
            }
        }

        /// <summary>
        /// Handles session state changes (disconnection, expiration, etc.)
        /// </summary>
        /// <param name="processName">The name of the process whose session changed state</param>
        /// <param name="isExpired">Whether the session expired</param>
        /// <param name="isDisconnected">Whether the session disconnected</param>
        public void OnSessionStateChanged(string processName, bool isExpired = false, bool isDisconnected = false)
        {
            if (string.IsNullOrWhiteSpace(processName)) return;

            string normalizedName = processName.ToLowerInvariant();

            if (_appToControlMap.TryGetValue(normalizedName, out var control) && control != null)
            {
                try
                {
                    if (Application.Current?.Dispatcher?.CheckAccess() == false)
                    {
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            HandleSessionStateChange(control, normalizedName, isExpired, isDisconnected);
                        });
                    }
                    else
                    {
                        HandleSessionStateChange(control, normalizedName, isExpired, isDisconnected);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[EventManager] Error handling state change for {normalizedName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets debugging information about current mappings
        /// </summary>
        /// <returns>String with mapping information</returns>
        public string GetMappingDebugInfo()
        {
            lock (_mappingLock)
            {
                var appCount = _appToControlMap.Count;
                var controlCount = _controlToAppsMap.Count;
                return $"EventManager: {appCount} apps mapped to {controlCount} controls";
            }
        }

        /// <summary>
        /// Clears all mappings (used during application shutdown)
        /// </summary>
        public void ClearAllMappings()
        {
            lock (_mappingLock)
            {
                _appToControlMap.Clear();
                _controlToAppsMap.Clear();
                Debug.WriteLine("[EventManager] Cleared all mappings");
            }
        }

        #region Private Methods

        private void SafeSetMuted(ChannelControl control, bool isMuted, string appName)
        {
            try
            {
                control.SetMuted(isMuted);
                Debug.WriteLine($"[EventManager] Updated mute state for {appName}: {isMuted}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EventManager] Failed to set mute state for {appName}: {ex.Message}");
            }
        }

        private void HandleSessionStateChange(ChannelControl control, string appName, bool isExpired, bool isDisconnected)
        {
            try
            {
                if (isExpired)
                {
                    control.HandleSessionExpired();
                    Debug.WriteLine($"[EventManager] Handled session expired for {appName}");
                }
                else if (isDisconnected)
                {
                    control.HandleSessionDisconnected();
                    Debug.WriteLine($"[EventManager] Handled session disconnected for {appName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EventManager] Failed to handle state change for {appName}: {ex.Message}");
            }
        }

        private void ClearControlFromMappings(ChannelControl control)
        {
            string controlKey = GetControlKey(control);

            // Remove all apps that were mapped to this control
            if (_controlToAppsMap.TryGetValue(controlKey, out var appsForControl))
            {
                foreach (var app in appsForControl)
                {
                    _appToControlMap.TryRemove(app, out _);
                }
            }

            // Remove the control from the control map
            _controlToAppsMap.TryRemove(controlKey, out _);
        }

        private string GetControlKey(ChannelControl control)
        {
            // Use the control's hash code as a unique identifier
            // This is safe because we only use it while the control exists
            return control.GetHashCode().ToString();
        }

        #endregion
    }
}
