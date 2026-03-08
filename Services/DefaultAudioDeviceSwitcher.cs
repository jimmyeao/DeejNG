using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Runtime.InteropServices;


namespace DeejNG.Services
{
    public class DefaultAudioDeviceSwitcher
    {
        private readonly MMDeviceEnumerator _enumerator = new();

        public void LogAudioOutDevices()
        {
            try
            {
                Debug.WriteLine("==== AUDIO OUTPUT DEVICES ====");

                var defaultDevice = _enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia);

                var devices = _enumerator.EnumerateAudioEndPoints(
                    DataFlow.Render,
                    DeviceState.Active);

                foreach (var device in devices)
                {
                    bool isDefault = defaultDevice.ID == device.ID;

                    Debug.WriteLine("------------------------------------");
                    Debug.WriteLine($"Name      : {device.FriendlyName}");
                    Debug.WriteLine($"ID        : {device.ID}");
                    Debug.WriteLine($"State     : {device.State}");
                    Debug.WriteLine($"Default   : {isDefault}");
                }

                Debug.WriteLine("====================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error listing audio devices: {ex.Message}");
            }
        }

        public bool TrySetDefaultOutput(string? deviceId, string? friendlyName = null)
        {
            // 1) Try by ID (best)
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var byId = _enumerator
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .FirstOrDefault(d => string.Equals(d.ID, deviceId, StringComparison.OrdinalIgnoreCase));

                if (byId != null)
                    return TrySetDefaultDeviceId(byId.ID);
            }

            // 2) Fallback by FriendlyName (repair path)
            if (!string.IsNullOrWhiteSpace(friendlyName))
            {
                var byName = _enumerator
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .FirstOrDefault(d => string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

                if (byName != null)
                {
                    // Optional: log so you know config was “repaired”
                    Debug.WriteLine($"Preset ID missing/changed, matched by name: '{friendlyName}' -> {byName.ID}");
                    return TrySetDefaultDeviceId(byName.ID);
                }
            }

            Console.WriteLine($"Could not find output device. id='{deviceId}', name='{friendlyName}'");
            return false;
        }

        public bool TrySetDefaultOutputByFriendlyName(string friendlyName)
        {
            var device = _enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

            if (device == null) return false;

            return TrySetDefaultDeviceId(device.ID);
        }

        public bool TrySetDefaultDeviceId(string deviceId)
        {
            try
            {
                var policy = (IPolicyConfig)new PolicyConfigClient();

                // Set for all roles so Windows, games, calls, etc follow.
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eConsole));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eCommunications));
                return true;
            }
            catch
            {
                return false;
            }
        }



        public bool TrySetDefaultInput(string? deviceId, string? friendlyName = null)
        {
            // 1) Try by ID (best)
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var byId = _enumerator
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => string.Equals(d.ID, deviceId, StringComparison.OrdinalIgnoreCase));

                if (byId != null)
                    return TrySetDefaultCaptureDeviceId(byId.ID);
            }

            // 2) Fallback by FriendlyName (repair path)
            if (!string.IsNullOrWhiteSpace(friendlyName))
            {
                var byName = _enumerator
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

                if (byName != null)
                {
                    Debug.WriteLine($"Preset ID missing/changed, matched input by name: '{friendlyName}' -> {byName.ID}");
                    return TrySetDefaultCaptureDeviceId(byName.ID);
                }
            }

            Debug.WriteLine($"Could not find input device. id='{deviceId}', name='{friendlyName}'");
            return false;
        }

        public bool TrySetDefaultInputByFriendlyName(string friendlyName)
        {
            var device = _enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

            if (device == null)
                return false;

            return TrySetDefaultCaptureDeviceId(device.ID);
        }

        public bool TrySetDefaultCaptureDeviceId(string deviceId)
        {
            try
            {
                var policy = (IPolicyConfig)new PolicyConfigClient();

                // Set for all roles so apps follow consistently.
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eConsole));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eCommunications));

                return true;
            }
            catch
            {
                return false;
            }
        }



    }
    internal enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    // CLSID commonly used for PolicyConfigClient (works on Win10/11 in many projects)
    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        // Many methods exist; we only need SetDefaultEndpoint.
        // Keep method order/signature consistent with known implementations.
        int GetMixFormat();
        int GetDeviceFormat();
        int ResetDeviceFormat();
        int SetDeviceFormat();
        int GetProcessingPeriod();
        int SetProcessingPeriod();
        int GetShareMode();
        int SetShareMode();
        int GetPropertyValue();
        int SetPropertyValue();
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole eRole);
        int SetEndpointVisibility();
    }
}
