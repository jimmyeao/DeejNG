using DeejNG.Classes;
using System;
using System.Text.Json;

namespace DeejNG.Models
{
    /// <summary>
    /// Represents a user profile containing all application settings
    /// </summary>
    public class Profile
    {
        #region Public Properties

        /// <summary>
        /// Gets or sets the timestamp when this profile was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Gets or sets the timestamp when this profile was last modified
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// Gets or sets the unique name of the profile (e.g., "Gaming", "Streaming", "Default")
        /// </summary>
        public string Name { get; set; } = "Default";

        /// <summary>
        /// Gets or sets the application settings for this profile
        /// </summary>
        public AppSettings Settings { get; set; } = new AppSettings();

        #endregion Public Properties

        #region Public Methods

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

        #endregion Public Methods

        #region Private Methods

        private AppSettings CloneSettings(AppSettings original)
        {
            // Deep copy through serialization so no setting (buttons, theme, connection mode, etc.) is lost
            var options = new JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            var json = JsonSerializer.Serialize(original ?? new AppSettings(), options);
            return JsonSerializer.Deserialize<AppSettings>(json, options) ?? new AppSettings();
        }

        #endregion Private Methods
    }
}
