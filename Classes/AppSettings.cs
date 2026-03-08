using DeejNG.Models;

namespace DeejNG.Classes
{
    /// <summary>
    /// Represents application settings for DeejNG.
    /// </summary>
    public class AppSettings
    {
        #region Constants
        /// <summary>
        /// Default timeout in seconds for the overlay.
        /// </summary>
        private const int DefaultOverlayTimeoutSeconds = 5;

        /// <summary>
        /// Constant indicating no timeout for the overlay.
        /// </summary>
        public const int OverlayNoTimeout = 0;
        #endregion

        #region Public Properties
        /// <summary>
        /// Gets or sets a value indicating whether slider smoothing is disabled.
        /// </summary>
        public bool DisableSmoothing { get; set; }

        /// <summary>
        /// Gets or sets the input modes for each slider.
        /// </summary>
        public List<bool> InputModes { get; set; } = new();

        /// <summary>
        /// Gets or sets a value indicating whether the dark theme is enabled.
        /// </summary>
        public bool IsDarkTheme { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the slider is inverted.
        /// </summary>
        public bool IsSliderInverted { get; set; }

        /// <summary>
        /// Gets or sets the mute states for each slider.
        /// </summary>
        public List<bool> MuteStates { get; set; } = new();

        /// <summary>
        /// Gets or sets the name of the port used for communication.
        /// </summary>
        public string? PortName { get; set; }

        /// <summary>
        /// Gets or sets the audio targets assigned to each slider.
        /// Extra Note, in here we have also the applications
        /// </summary>
        public List<List<AudioTarget>> SliderTargets { get; set; } = new();

        /// <summary>
        /// Gets or sets a value indicating whether the application should start minimized.
        /// </summary>
        public bool StartMinimized { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether the application should start on system boot.
        /// </summary>
        public bool StartOnBoot { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the application should use an exponential volume curve.
        /// </summary>
        public bool UseExponentialVolume { get; set; }

        /// <summary>
        /// The factor to use for the exponential volume curve. A higher factor makes the curve more agressive.
        /// </summary>
        public float ExponentialVolumeFactor { get; set; } = 2;

        /// <summary>
        /// Gets or sets a value indicating whether VU meters are enabled.
        /// </summary>
        public bool VuMeters { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the overlay is enabled.
        /// </summary>
        public bool OverlayEnabled { get; set; } = false;

        /// <summary>
        /// Gets or sets the timeout in seconds for the overlay.
        /// </summary>
        public int OverlayTimeoutSeconds { get; set; } = DefaultOverlayTimeoutSeconds;

        /// <summary>
        /// Gets or sets the X position of the overlay.
        /// </summary>
        public double OverlayX { get; set; }

        /// <summary>
        /// Gets or sets the Y position of the overlay.
        /// </summary>
        public double OverlayY { get; set; }

        /// <summary>
        /// Gets or sets the device name of the screen the overlay was on.
        /// Used to restore overlay to the correct monitor in multi-screen setups.
        /// </summary>
        public string OverlayScreenDevice { get; set; }

        /// <summary>
        /// Gets or sets the working area bounds of the screen the overlay was on.
        /// Format: "Left,Top,Width,Height". Used for validation in multi-monitor scenarios.
        /// </summary>
        public string OverlayScreenBounds { get; set; }

        /// <summary>
        /// Gets or sets the opacity of the overlay.
        /// </summary>
        public double OverlayOpacity { get; set; } = 0.85;

        /// <summary>
        /// Gets or sets the text color for the overlay. Possible values: "Auto", "White", "Black".
        /// </summary>
        public string OverlayTextColor { get; set; } = "Auto"; // "Auto", "White", "Black"

        /// <summary>
        /// Gets or sets the selected theme name.
        /// </summary>
        public string? SelectedTheme { get; set; } = "Dark";

        /// <summary>
        /// Gets or sets the number of physical buttons connected to the controller.
        /// Default is 0 (no buttons).
        /// </summary>
        public int NumberOfButtons { get; set; } = 0;

        /// <summary>
        /// Gets or sets the button mappings for physical buttons.
        /// </summary>
        public List<ButtonMapping> ButtonMappings { get; set; } = new();

        /// <summary>
        /// Gets or sets the baud rate used for the serial connection.
        /// </summary>
        public int BaudRate { get; set; } = 9600;

        /// <summary>
        /// Gets or sets the Vendor ID of the HID device.
        /// </summary>
        public int HidVendorId { get; set; }

        /// <summary>
        /// Gets or sets the Product ID of the HID device.
        /// </summary>
        public int HidProductId { get; set; }





        /// <summary>
        /// Gets or sets audio device preset 1 friendly name.
        /// </summary>
        public string AudioDeviceOneFriendlyName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets audio device preset 1 device ID.
        /// </summary>
        public string AudioDeviceOneId { get; set; } = string.Empty;



        /// <summary>
        /// Gets or sets microhpone device preset 1 friendly name.
        /// </summary>
        public string MicrophoneDeviceOneFriendlyName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets microhpone device preset 1 device ID.
        /// </summary>
        public string MicrophoneDeviceOneId { get; set; } = string.Empty;




        /// <summary>
        /// Gets or sets audio device preset 2 friendly name.
        /// </summary>
        public string AudioDeviceTwoFriendlyName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets audio device preset 2 device ID.
        /// </summary>
        public string AudioDeviceTwoId { get; set; } = string.Empty;



        /// <summary>
        /// Gets or sets microhpone device preset 2 friendly name.
        /// </summary>
        public string MicrophoneDeviceTwoFriendlyName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets microhpone device preset 2 device ID.
        /// </summary>
        public string MicrophoneDeviceTwoId { get; set; } = string.Empty;





        /// <summary>
        /// Gets or sets audio device preset 3 friendly name.
        /// </summary>
        public string AudioDeviceThreeFriendlyName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets audio device preset 3 device ID.
        /// </summary>
        public string AudioDeviceThreeId { get; set; } = string.Empty;



        /// <summary>
        /// Gets or sets microhpone device preset 3 friendly name.
        /// </summary>
        public string MicrophoneDeviceThreeFriendlyName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets microhpone device preset 3 device ID.
        /// </summary>
        public string MicrophoneDeviceThreeId { get; set; } = string.Empty;






        /// <summary>
        /// Gets or sets the list of applications excluded from "Unmapped Applications" control.
        /// These apps will not be affected by the unmapped slider even if they aren't assigned to any slider.
        /// </summary>

        public List<string> ExcludedFromUnmapped { get; set; } = new();
        #endregion Public Properties
    }

}
