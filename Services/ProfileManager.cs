using DeejNG.Classes;
using DeejNG.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeejNG.Services
{
    /// <summary>
    /// Manages user profiles including loading, saving, switching, and migration
    /// </summary>
    public class ProfileManager
    {
        #region Private Fields

        private readonly object _profileLock = new object();
        private readonly AppSettingsManager _settingsManager;
        private string _cachedProfilesPath = null;
        private ProfileCollection _profileCollection = new ProfileCollection();

        #endregion Private Fields

        #region Public Constructors

        public ProfileManager(AppSettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        }

        #endregion Public Constructors

        #region Public Events

        public event Action<Profile> ProfileChanged;

        #endregion Public Events

        #region Public Properties

        /// <summary>
        /// Gets the currently active profile
        /// </summary>
        public Profile ActiveProfile => _profileCollection.GetActiveProfile();

        /// <summary>
        /// Gets the current profile collection
        /// </summary>
        public ProfileCollection ProfileCollection => _profileCollection;

        #endregion Public Properties

        #region Private Properties

        /// <summary>
        /// Gets the profiles file path with fallback options for compatibility
        /// </summary>
        private string ProfilesPath
        {
            get
            {
                if (_cachedProfilesPath != null)
                    return _cachedProfilesPath;

                // Use same directory as settings file
                string settingsDir = Path.GetDirectoryName(_settingsManager.GetSettingsPath());
                _cachedProfilesPath = Path.Combine(settingsDir, "profiles.json");


                return _cachedProfilesPath;
            }
        }

        #endregion Private Properties

        #region Public Methods

        /// <summary>
        /// Creates a new profile
        /// </summary>
        public bool CreateProfile(string name, bool copyFromActive = true)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            Profile newProfile;

            if (copyFromActive)
            {
                // Clone the active profile
                var activeProfile = _profileCollection.GetActiveProfile();
                newProfile = activeProfile.Clone();
                newProfile.Name = name;
            }
            else
            {
                // Create a fresh profile
                newProfile = new Profile
                {
                    Name = name,
                    Settings = new AppSettings()
                };
            }

            if (_profileCollection.AddProfile(newProfile))
            {
                SaveProfiles();

                return true;
            }


            return false;
        }

        /// <summary>
        /// Deletes a profile
        /// </summary>
        public bool DeleteProfile(string name)
        {
            if (_profileCollection.RemoveProfile(name))
            {
                SaveProfiles();

                return true;
            }


            return false;
        }

        /// <summary>
        /// Gets the settings for the active profile
        /// </summary>
        public AppSettings GetActiveProfileSettings()
        {
            return _profileCollection.GetActiveProfile()?.Settings ?? new AppSettings();
        }

        /// <summary>
        /// Gets all profile names
        /// </summary>
        public List<string> GetProfileNames() => _profileCollection.Profiles.Select(p => p.Name).ToList();

        /// <summary>
        /// Loads profiles from disk, performing migration if necessary
        /// </summary>
        public void LoadProfiles()
        {
            try
            {
                if (File.Exists(ProfilesPath))
                {
                    // Load existing profiles
                    var json = File.ReadAllText(ProfilesPath);
                    _profileCollection = JsonSerializer.Deserialize<ProfileCollection>(json) ?? new ProfileCollection();


                }
                else
                {
                    // New installation or upgrade - perform migration
                    MigrateFromLegacySettings();
                }

                // Ensure we have at least one profile
                if (_profileCollection.Profiles.Count == 0)
                {
                    var defaultProfile = new Profile
                    {
                        Name = "Default",
                        Settings = new AppSettings()
                    };
                    _profileCollection.Profiles.Add(defaultProfile);
                    _profileCollection.ActiveProfileName = "Default";


                }
            }
            catch (Exception ex)
            {

                // Create default profile on error
                _profileCollection = new ProfileCollection();
                _profileCollection.Profiles.Add(new Profile { Name = "Default" });
                _profileCollection.ActiveProfileName = "Default";
            }
        }

        /// <summary>
        /// Renames a profile
        /// </summary>
        public bool RenameProfile(string oldName, string newName)
        {
            if (_profileCollection.RenameProfile(oldName, newName))
            {
                SaveProfiles();

                return true;
            }


            return false;
        }

        /// <summary>
        /// Saves all profiles to disk
        /// </summary>
        public void SaveProfiles()
        {
            lock (_profileLock)
            {
                try
                {
                    // Update last modified time for active profile
                    var activeProfile = _profileCollection.GetActiveProfile();
                    if (activeProfile != null)
                    {
                        activeProfile.LastModified = DateTime.Now;
                    }

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    var json = JsonSerializer.Serialize(_profileCollection, options);

                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(ProfilesPath);
                    if (!Directory.Exists(dir) && dir != null)
                    {
                        Directory.CreateDirectory(dir);
                    }

                    // Write with explicit flush for Server 2022 compatibility
                    using (var fileStream = new FileStream(ProfilesPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(fileStream))
                    {
                        writer.Write(json);
                        writer.Flush();
                        fileStream.Flush(true);
                    }


                }
                catch (Exception ex)
                {

                }
            }
        }

        /// <summary>
        /// Saves profiles asynchronously
        /// </summary>
        public void SaveProfilesAsync()
        {
            Task.Run(() => SaveProfiles());
        }

        /// <summary>
        /// Switches to a different profile
        /// </summary>
        public bool SwitchToProfile(string profileName)
        {
            if (_profileCollection.SetActiveProfile(profileName))
            {
                SaveProfiles();
                ProfileChanged?.Invoke(_profileCollection.GetActiveProfile());

                return true;
            }


            return false;
        }

        /// <summary>
        /// Updates the settings for the active profile
        /// </summary>
        public void UpdateActiveProfileSettings(AppSettings settings)
        {
            var activeProfile = _profileCollection.GetActiveProfile();
            if (activeProfile != null)
            {
                activeProfile.Settings = settings;
                activeProfile.LastModified = DateTime.Now;
            }
        }

        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Migrates settings from old settings.json to new profile system
        /// </summary>
        private void MigrateFromLegacySettings()
        {
            try
            {
                // Try to load existing settings
                var legacySettings = _settingsManager.LoadSettingsFromDisk();

                if (legacySettings != null)
                {
                    // Create a "Default" profile from existing settings
                    var defaultProfile = new Profile
                    {
                        Name = "Default",
                        Settings = legacySettings,
                        CreatedAt = DateTime.Now,
                        LastModified = DateTime.Now
                    };

                    _profileCollection.Profiles.Add(defaultProfile);
                    _profileCollection.ActiveProfileName = "Default";



                    // Save the new profile system
                    SaveProfiles();

                    // Optionally rename old settings file to indicate migration
                    try
                    {
                        string legacyPath = _settingsManager.GetSettingsPath();
                        if (File.Exists(legacyPath))
                        {
                            string backupPath = Path.Combine(
                                Path.GetDirectoryName(legacyPath),
                                "settings.json.backup");
                            File.Move(legacyPath, backupPath);

                        }
                    }
                    catch (Exception ex)
                    {

                    }
                }
                else
                {

                }
            }
            catch (Exception ex)
            {

            }
        }

        #endregion Private Methods
    }
}
