using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace DeejNG.Services
{
    public class DeviceCacheManager : IDisposable
    {
        #region Private Fields

        private const int CacheRefreshIntervalSeconds = 30;
        private const float VolumeChangeThreshold = 0.01f;
        private static readonly TimeSpan RemovalBackoff = TimeSpan.FromSeconds(5);
        private readonly object _cacheLock = new object();
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        // Backoff handling to avoid repeated work on removed devices
        private readonly Dictionary<string, DateTime> _inputBackoffUntil = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, MMDevice> _inputDeviceMap = new();
        private readonly Dictionary<string, float?> _lastInputVolume = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _outputBackoffUntil = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MMDevice> _outputDeviceMap = new();
        private DateTime _lastDeviceCacheTime = DateTime.MinValue;

        #endregion Private Fields

        #region Public Constructors

        public DeviceCacheManager()
        {
            BuildInputDeviceCache();
            BuildOutputDeviceCache();
        }

        #endregion Public Constructors

        #region Public Methods

        public void ApplyInputDeviceVolume(string deviceName, float level, bool isMuted)
        {
            var key = deviceName.ToLowerInvariant();
            if (_inputBackoffUntil.TryGetValue(key, out var until) && DateTime.Now < until)
                return;

            if (!_inputDeviceMap.TryGetValue(key, out var mic))
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

                        RemoveInputDevice(deviceName);
                        _inputBackoffUntil[key] = DateTime.Now + RemovalBackoff;
                    }
                }
            }
        }

        public void ApplyOutputDeviceVolume(string deviceName, float level, bool isMuted)
        {
            var key = deviceName.ToLowerInvariant();
            if (_outputBackoffUntil.TryGetValue(key, out var until) && DateTime.Now < until)
                return;

            if (!_outputDeviceMap.TryGetValue(key, out var spkr))
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

                    RemoveOutputDevice(deviceName);
                    _outputBackoffUntil[key] = DateTime.Now + RemovalBackoff;
                }
            }
        }

        public void Dispose()
        {
            _deviceEnumerator?.Dispose();
            _inputDeviceMap.Clear();
            _outputDeviceMap.Clear();
            _lastInputVolume.Clear();
            _inputBackoffUntil.Clear();
            _outputBackoffUntil.Clear();
        }

        public MMDevice GetInputDevice(string deviceName)
        {
            var key = deviceName.ToLowerInvariant();

            // Respect backoff window
            if (_inputBackoffUntil.TryGetValue(key, out var until) && DateTime.Now < until)
                return null;

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

            // Respect backoff window
            if (_outputBackoffUntil.TryGetValue(key, out var until) && DateTime.Now < until)
                return null;

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
        public void RefreshCaches()
        {
            lock (_cacheLock)
            {
                try
                {
                    _inputDeviceMap.Clear();
                    _outputDeviceMap.Clear();
                    _lastInputVolume.Clear(); // Clear the volume cache too
                    _inputBackoffUntil.Clear();
                    _outputBackoffUntil.Clear();
                    BuildInputDeviceCache();
                    BuildOutputDeviceCache();
                    _lastDeviceCacheTime = DateTime.Now;

                }
                catch (Exception ex)
                {

                }
            }
        }

        public bool ShouldRefreshCache()
        {
            return (DateTime.Now - _lastDeviceCacheTime).TotalSeconds > CacheRefreshIntervalSeconds;
        }

        // NEW: Safe helpers for meter reads to avoid repeated exceptions on unplugged devices
        public bool TryGetInputPeak(string deviceName, out float peak, out bool isMuted)
        {
            peak = 0f;
            isMuted = true;
            var dev = GetInputDevice(deviceName);
            if (dev == null) return false;
            try
            {
                peak = dev.AudioMeterInformation.MasterPeakValue;
                isMuted = dev.AudioEndpointVolume.Mute;
                return true;
            }
            catch (ArgumentException)
            {
                RemoveInputDevice(deviceName);
                _inputBackoffUntil[deviceName.ToLowerInvariant()] = DateTime.Now + RemovalBackoff;
                return false;
            }
            catch (Exception)
            {
                RemoveInputDevice(deviceName);
                _inputBackoffUntil[deviceName.ToLowerInvariant()] = DateTime.Now + RemovalBackoff;
                return false;
            }
        }

        public bool TryGetOutputPeak(string deviceName, out float peak, out bool isMuted)
        {
            peak = 0f;
            isMuted = true;
            var dev = GetOutputDevice(deviceName);
            if (dev == null) return false;
            try
            {
                peak = dev.AudioMeterInformation.MasterPeakValue;
                isMuted = dev.AudioEndpointVolume.Mute;
                return true;
            }
            catch (ArgumentException)
            {
                RemoveOutputDevice(deviceName);
                _outputBackoffUntil[deviceName.ToLowerInvariant()] = DateTime.Now + RemovalBackoff;
                return false;
            }
            catch (Exception)
            {
                RemoveOutputDevice(deviceName);
                _outputBackoffUntil[deviceName.ToLowerInvariant()] = DateTime.Now + RemovalBackoff;
                return false;
            }
        }

        #endregion Public Methods

        #region Private Methods

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

            }
            catch (Exception ex)
            {

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

            }
            catch (Exception ex)
            {

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

        #endregion Private Methods
    }
}
