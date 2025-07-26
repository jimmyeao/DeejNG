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
            lock (_cacheLock)
            {
                if (_inputDeviceMap.TryGetValue(deviceName.ToLowerInvariant(), out var device))
                {
                    return device;
                }

                // Try to find and cache the device
                var foundDevice = _deviceEnumerator
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

                if (foundDevice != null)
                {
                    _inputDeviceMap[deviceName.ToLowerInvariant()] = foundDevice;
                }

                return foundDevice;
            }
        }

        public MMDevice GetOutputDevice(string deviceName)
        {
            lock (_cacheLock)
            {
                if (_outputDeviceMap.TryGetValue(deviceName.ToLowerInvariant(), out var device))
                {
                    return device;
                }

                // Try to find and cache the device
                var foundDevice = _deviceEnumerator
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

                if (foundDevice != null)
                {
                    _outputDeviceMap[deviceName.ToLowerInvariant()] = foundDevice;
                }

                return foundDevice;
            }
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
                        Debug.WriteLine($"[ERROR] Setting input device volume for '{deviceName}': {ex.Message}");
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
                    Debug.WriteLine($"[ERROR] Setting output device volume for '{deviceName}': {ex.Message}");
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
                    Debug.WriteLine("[DeviceCache] Caches refreshed");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DeviceCache] Error refreshing caches: {ex.Message}");
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
                Debug.WriteLine($"[DeviceCache] Cached {_inputDeviceMap.Count} input devices.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DeviceCache] Failed to build input device cache: {ex.Message}");
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
                Debug.WriteLine($"[DeviceCache] Cached {_outputDeviceMap.Count} output devices.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DeviceCache] Failed to build output device cache: {ex.Message}");
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