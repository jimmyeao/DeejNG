using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace DeejNG.Infrastructure.System
{
    public class SystemIntegrationService : ISystemIntegrationService
    {
        #region Private Fields

        private const string APP_NAME = "DeejNG";

        #endregion Private Fields

        #region Public Methods

        public void DisableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key?.DeleteValue(APP_NAME, false);

            }
            catch (Exception ex)
            {

                MessageBox.Show($"Failed to disable startup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void EnableStartup()
        {
            try
            {
                // Option 1: Try to find the correct shortcut path
                string shortcutPath = FindApplicationShortcut();
                
                if (!string.IsNullOrEmpty(shortcutPath) && File.Exists(shortcutPath))
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    key?.SetValue(APP_NAME, $"\"{shortcutPath}\"", RegistryValueKind.String);

                }
                else
                {
                    // Option 2: Use the current executable path as fallback
                    string exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                    if (File.Exists(exePath))
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                        key?.SetValue(APP_NAME, $"\"{exePath}\"", RegistryValueKind.String);

                    }
                    else
                    {
                        throw new FileNotFoundException("Unable to locate application executable or shortcut for startup registration.");
                    }
                }
            }
            catch (Exception ex)
            {

                MessageBox.Show($"Failed to enable startup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        public bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                var value = key?.GetValue(APP_NAME) as string;
                bool isEnabled = !string.IsNullOrEmpty(value);

                return isEnabled;
            }
            catch (Exception ex)
            {

                return false;
            }
        }

        public void SetDisplayIcon()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (!File.Exists(exePath))
                {

                    return;
                }

                var myUninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                string[]? mySubKeyNames = myUninstallKey?.GetSubKeyNames();
                
                for (int i = 0; i < mySubKeyNames?.Length; i++)
                {
                    using var myKey = myUninstallKey?.OpenSubKey(mySubKeyNames[i], true);
                    var displayName = (string?)myKey?.GetValue("DisplayName");
                    
                    if (displayName?.Contains(APP_NAME) == true)
                    {
                        myKey?.SetValue("DisplayIcon", exePath + ",0");

                        break;
                    }
                }
            }
            catch (Exception ex)
            {

                // Don't show message box for icon errors as they're not critical
            }
        }

        #endregion Public Methods

        #region Private Methods

        private string FindApplicationShortcut()
        {
            try
            {
                // Try multiple possible shortcut locations
                string[] possiblePaths = {
                    // Option 1: Direct path as user suggested
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Microsoft", "Windows", "Start Menu", "Programs",
                        "DeejNG",
                        "DeejNG.appref-ms"),
                    
                    // Option 2: With Jimmy White folder
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Microsoft", "Windows", "Start Menu", "Programs",
                        "Jimmy White",
                        "DeejNG",
                        "DeejNG.appref-ms"),
                    
                    // Option 3: Different Jimmy White structure
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Microsoft", "Windows", "Start Menu", "Programs",
                        "Jimmy White's DeejNG",
                        "DeejNG.appref-ms"),
                    
                    // Option 4: Look for .lnk shortcut instead
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Microsoft", "Windows", "Start Menu", "Programs",
                        "DeejNG.lnk"),
                    
                    // Option 5: Common programs folder
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                        "DeejNG",
                        "DeejNG.lnk"),
                    
                    // Option 6: User's programs folder
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                        "DeejNG",
                        "DeejNG.lnk")
                };

                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {

                        return path;
                    }
                }


            }
            catch (Exception ex)
            {

            }

            return string.Empty;
        }

        #endregion Private Methods
    }
}
