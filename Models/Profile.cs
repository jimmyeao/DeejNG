using DeejNG.Classes;
using System;

namespace DeejNG.Models
{
    /// <summary>
    /// Represents a user profile containing all application settings
    /// </summary>
    public class Profile
    {
        /// <summary>
        /// Gets or sets the unique name of the profile (e.g., "Gaming", "Streaming", "Default")
        /// </summary>
        public string Name { get; set; } = "Default";

        /// <summary>
        /// Gets or sets the application settings for this profile
        /// </summary>
        public AppSettings Settings { get; set; } = new AppSettings();

        /// <summary>
        /// Gets or sets the timestamp when this profile was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Gets or sets the timestamp when this profile was last modified
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// Creates a deep copy of this profile
        /// </summary>
        public Profile Clone()
        {
            return new Profile
            {
                Name = this.Name,
                Settings = CloneSettings(this.Settings),
                CreatedAt = this.CreatedAt,
                LastModified = DateTime.Now
            };
        }

        private AppSettings CloneSettings(AppSettings original)
        {
            // Create a new AppSettings with all values copied
            return new AppSettings
            {
                PortName = original.PortName,
                SliderTargets = original.SliderTargets?.Select(list =>
                    list?.Select(target => new AudioTarget
                    {
                        Name = target.Name,
                        IsInputDevice = target.IsInputDevice,
                        IsOutputDevice = target.IsOutputDevice
                    }).ToList() ?? new List<AudioTarget>()
                ).ToList() ?? new List<List<AudioTarget>>(),
                IsDarkTheme = original.IsDarkTheme,
                IsSliderInverted = original.IsSliderInverted,
                VuMeters = original.VuMeters,
                StartOnBoot = original.StartOnBoot,
                StartMinimized = original.StartMinimized,
                DisableSmoothing = original.DisableSmoothing,
                InputModes = original.InputModes?.ToList() ?? new List<bool>(),
                MuteStates = original.MuteStates?.ToList() ?? new List<bool>(),
                OverlayEnabled = original.OverlayEnabled,
                OverlayTimeoutSeconds = original.OverlayTimeoutSeconds,
                OverlayX = original.OverlayX,
                OverlayY = original.OverlayY,
                OverlayScreenDevice = original.OverlayScreenDevice,
                OverlayScreenBounds = original.OverlayScreenBounds,
                OverlayOpacity = original.OverlayOpacity,
                OverlayTextColor = original.OverlayTextColor
            };
        }
    }
}
