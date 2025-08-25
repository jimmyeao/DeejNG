using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace DeejNG.Services
{
    public class DeviceCacheManager : IDisposable
    {
        private readonly Dictionary<string, MMDevice> _inputDeviceMap = new();
        private readonly Dictionary<string, MMDevice> _outputDeviceMap = new();
        private readonly Dictionary<string, float?> _lastInputVolume = new(StringComparer.OrdinalIgnoreCase);
        private const int CacheRefreshIntervalSeconds = 30;
        private const float VolumeChangeThreshold = 0.01f;
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private DateTime _lastDeviceCacheTime = DateTime.MinValue;
        private readonly object _cacheLock = new object();

        public DeviceCacheManager()
        {
            BuildInputDeviceCache();
            BuildOutputDeviceCache();
        }

        public MMDevice GetInputDevice(string deviceName)
        {
            var key = deviceName.ToLowerInvariant();

            // First check: Quick cache lookup
            lock (_cacheLock)
            {
                if (_inputDeviceMap.TryGetValue(key, out var cachedDevice))
                {
                    return cachedDevice;
                }
            }

            // Expensive operation outside lock to prevent blocking other threads
            var foundDevice = _deviceEnumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

            // Second lock: Add to cache if found
            if (foundDevice != null)
            {
                lock (_cacheLock)
                {
                    // Use TryAdd to handle race conditions gracefully
                    _inputDeviceMap.TryAdd(key, foundDevice);
                }
            }

            return foundDevice;
        }

        public MMDevice GetOutputDevice(string deviceName)
        {
            var key = deviceName.ToLowerInvariant();

            // First check: Quick cache lookup
            lock (_cacheLock)
            {
                if (_outputDeviceMap.TryGetValue(key, out var cachedDevice))
                {
                    return cachedDevice;
                }
            }

            // Expensive operation outside lock to prevent blocking other threads
            var foundDevice = _deviceEnumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

            // Second lock: Add to cache if found
            if (foundDevice != null)
            {
                lock (_cacheLock)
                {
                    // Use TryAdd to handle race conditions gracefully
                    _outputDeviceMap.TryAdd(key, foundDevice);
                }
            }

            return foundDevice;
        }

        public void ApplyInputDeviceVolume(string deviceName, float level, bool isMuted)
        {
            if (!_inputDeviceMap.TryGetValue(deviceName.ToLowerInvariant(), out var mic))
            {
                mic = GetInputDevice(deviceName);
            }

            if (mic != null)
            {
                // FIX: Use nullable float instead of -1f sentinel
                float? previous = _lastInputVolume.TryGetValue(deviceName, out var lastVol) ? lastVol : null;

                // Check if this is first time setting volume or if volume changed significantly
                if (previous == null || Math.Abs(previous.Value - level) > VolumeChangeThreshold)
                {
                    try
                    {
                        mic.AudioEndpointVolume.Mute = isMuted || level <= VolumeChangeThreshold;
                        mic.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                        _lastInputVolume[deviceName] = level;
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine($"[ERROR] Setting input device volume for '{deviceName}': {ex.Message}");
#endif
                        RemoveInputDevice(deviceName);
                    }
                }
            }
        }

        public void ApplyOutputDeviceVolume(string deviceName, float level, bool isMuted)
        {
            if (!_outputDeviceMap.TryGetValue(deviceName.ToLowerInvariant(), out var spkr))
            {
                spkr = GetOutputDevice(deviceName);
            }

            if (spkr != null)
            {
                try
                {
                    spkr.AudioEndpointVolume.Mute = isMuted;
                    spkr.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[ERROR] Setting output device volume for '{deviceName}': {ex.Message}");
#endif
                    RemoveOutputDevice(deviceName);
                }
            }
        }

        public void RefreshCaches()
        {
            lock (_cacheLock)
            {
                try
                {
                    _inputDeviceMap.Clear();
                    _outputDeviceMap.Clear();
                    _lastInputVolume.Clear(); // Clear the volume cache too
                    BuildInputDeviceCache();
                    BuildOutputDeviceCache();
                    _lastDeviceCacheTime = DateTime.Now;
#if DEBUG
                    Debug.WriteLine("[DeviceCache] Caches refreshed");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[DeviceCache] Error refreshing caches: {ex.Message}");
#endif
                }
            }
        }

        public bool ShouldRefreshCache()
        {
            return (DateTime.Now - _lastDeviceCacheTime).TotalSeconds > CacheRefreshIntervalSeconds;
        }

        private void BuildInputDeviceCache()
        {
            try
            {
                var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var device in devices)
                {
                    var key = device.FriendlyName.Trim().ToLowerInvariant();
                    if (!_inputDeviceMap.ContainsKey(key))
                    {
                        _inputDeviceMap[key] = device;
                    }
                }
#if DEBUG
                Debug.WriteLine($"[DeviceCache] Cached {_inputDeviceMap.Count} input devices.");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[DeviceCache] Failed to build input device cache: {ex.Message}");
#endif
            }
        }

        private void BuildOutputDeviceCache()
        {
            try
            {
                var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    var key = device.FriendlyName.Trim().ToLowerInvariant();
                    if (!_outputDeviceMap.ContainsKey(key))
                    {
                        _outputDeviceMap[key] = device;
                    }
                }
#if DEBUG
                Debug.WriteLine($"[DeviceCache] Cached {_outputDeviceMap.Count} output devices.");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[DeviceCache] Failed to build output device cache: {ex.Message}");
#endif
            }
        }

        private void RemoveInputDevice(string deviceName)
        {
            lock (_cacheLock)
            {
                _inputDeviceMap.Remove(deviceName.ToLowerInvariant());
            }
        }

        private void RemoveOutputDevice(string deviceName)
        {
            lock (_cacheLock)
            {
                _outputDeviceMap.Remove(deviceName.ToLowerInvariant());
            }
        }

        public void Dispose()
        {
            _deviceEnumerator?.Dispose();
            _inputDeviceMap.Clear();
            _outputDeviceMap.Clear();
            _lastInputVolume.Clear();
        }
    }
}
