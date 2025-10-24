using System;
using System.Collections.Generic;
using System.Linq;

namespace DeejNG.Models
{
    /// <summary>
    /// Container for all profiles and active profile tracking
    /// </summary>
    public class ProfileCollection
    {
        /// <summary>
        /// Gets or sets the name of the currently active profile
        /// </summary>
        public string ActiveProfileName { get; set; } = "Default";

        /// <summary>
        /// Gets or sets the list of all available profiles
        /// </summary>
        public List<Profile> Profiles { get; set; } = new List<Profile>();

        /// <summary>
        /// Gets the currently active profile
        /// </summary>
        public Profile GetActiveProfile()
        {
            var profile = Profiles.FirstOrDefault(p => p.Name == ActiveProfileName);

            // Fallback to first profile if active profile not found
            if (profile == null && Profiles.Count > 0)
            {
                profile = Profiles[0];
                ActiveProfileName = profile.Name;
            }

            // If still no profile, create a default one
            if (profile == null)
            {
                profile = new Profile { Name = "Default" };
                Profiles.Add(profile);
                ActiveProfileName = "Default";
            }

            return profile;
        }

        /// <summary>
        /// Sets the active profile by name
        /// </summary>
        public bool SetActiveProfile(string profileName)
        {
            var profile = Profiles.FirstOrDefault(p => p.Name == profileName);
            if (profile != null)
            {
                ActiveProfileName = profileName;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Adds a new profile to the collection
        /// </summary>
        public bool AddProfile(Profile profile)
        {
            if (Profiles.Any(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return false; // Profile with this name already exists
            }

            Profiles.Add(profile);
            return true;
        }

        /// <summary>
        /// Removes a profile by name
        /// </summary>
        public bool RemoveProfile(string profileName)
        {
            // Don't allow removing the last profile
            if (Profiles.Count <= 1)
            {
                return false;
            }

            var profile = Profiles.FirstOrDefault(p => p.Name == profileName);
            if (profile != null)
            {
                Profiles.Remove(profile);

                // If we removed the active profile, switch to the first available
                if (ActiveProfileName == profileName)
                {
                    ActiveProfileName = Profiles[0].Name;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Renames a profile
        /// </summary>
        public bool RenameProfile(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                return false;

            // Check if new name already exists
            if (Profiles.Any(p => p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase) &&
                                  !p.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var profile = Profiles.FirstOrDefault(p => p.Name == oldName);
            if (profile != null)
            {
                profile.Name = newName;
                profile.LastModified = DateTime.Now;

                if (ActiveProfileName == oldName)
                {
                    ActiveProfileName = newName;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets a profile by name
        /// </summary>
        public Profile GetProfile(string name)
        {
            return Profiles.FirstOrDefault(p => p.Name == name);
        }
    }
}
