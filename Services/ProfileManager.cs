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
        private readonly object _profileLock = new object();
        private string _cachedProfilesPath = null;
        private ProfileCollection _profileCollection = new ProfileCollection();
        private readonly AppSettingsManager _settingsManager;

        public event Action<Profile> ProfileChanged;

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

#if DEBUG
                Debug.WriteLine($"[Profiles] Profiles path: {_cachedProfilesPath}");
#endif
                return _cachedProfilesPath;
            }
        }

        public ProfileManager(AppSettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        }

        /// <summary>
        /// Gets the current profile collection
        /// </summary>
        public ProfileCollection ProfileCollection => _profileCollection;

        /// <summary>
        /// Gets the currently active profile
        /// </summary>
        public Profile ActiveProfile => _profileCollection.GetActiveProfile();

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

#if DEBUG
                    Debug.WriteLine($"[Profiles] Loaded {_profileCollection.Profiles.Count} profiles from disk");
                    Debug.WriteLine($"[Profiles] Active profile: {_profileCollection.ActiveProfileName}");
#endif
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

#if DEBUG
                    Debug.WriteLine("[Profiles] Created default profile");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[Profiles] Error loading profiles: {ex.Message}");
#endif
                // Create default profile on error
                _profileCollection = new ProfileCollection();
                _profileCollection.Profiles.Add(new Profile { Name = "Default" });
                _profileCollection.ActiveProfileName = "Default";
            }
        }

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

#if DEBUG
                    Debug.WriteLine("[Profiles] Migrated legacy settings to 'Default' profile");
                    Debug.WriteLine($"[Profiles]   - Port: {legacySettings.PortName}");
                    Debug.WriteLine($"[Profiles]   - Sliders: {legacySettings.SliderTargets?.Count ?? 0}");
#endif

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
#if DEBUG
                            Debug.WriteLine($"[Profiles] Backed up legacy settings to {backupPath}");
#endif
                        }
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine($"[Profiles] Could not backup legacy settings: {ex.Message}");
#endif
                    }
                }
                else
                {
#if DEBUG
                    Debug.WriteLine("[Profiles] No legacy settings found, creating fresh default profile");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[Profiles] Error during migration: {ex.Message}");
#endif
            }
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

#if DEBUG
                    Debug.WriteLine($"[Profiles] Saved {_profileCollection.Profiles.Count} profiles to disk");
                    Debug.WriteLine($"[Profiles] Active profile: {_profileCollection.ActiveProfileName}");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"[Profiles] Error saving profiles: {ex.Message}");
                    Debug.WriteLine($"[Profiles] Stack trace: {ex.StackTrace}");
#endif
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
#if DEBUG
                Debug.WriteLine($"[Profiles] Switched to profile: {profileName}");
#endif
                return true;
            }

#if DEBUG
            Debug.WriteLine($"[Profiles] Failed to switch to profile: {profileName}");
#endif
            return false;
        }

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
#if DEBUG
                Debug.WriteLine($"[Profiles] Created new profile: {name}");
#endif
                return true;
            }

#if DEBUG
            Debug.WriteLine($"[Profiles] Failed to create profile (already exists): {name}");
#endif
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
#if DEBUG
                Debug.WriteLine($"[Profiles] Deleted profile: {name}");
#endif
                return true;
            }

#if DEBUG
            Debug.WriteLine($"[Profiles] Failed to delete profile: {name}");
#endif
            return false;
        }

        /// <summary>
        /// Renames a profile
        /// </summary>
        public bool RenameProfile(string oldName, string newName)
        {
            if (_profileCollection.RenameProfile(oldName, newName))
            {
                SaveProfiles();
#if DEBUG
                Debug.WriteLine($"[Profiles] Renamed profile: {oldName} -> {newName}");
#endif
                return true;
            }

#if DEBUG
            Debug.WriteLine($"[Profiles] Failed to rename profile: {oldName} -> {newName}");
#endif
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

        /// <summary>
        /// Gets the settings for the active profile
        /// </summary>
        public AppSettings GetActiveProfileSettings()
        {
            return _profileCollection.GetActiveProfile()?.Settings ?? new AppSettings();
        }
    }
}
