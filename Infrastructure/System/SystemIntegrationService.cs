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
        private const string APP_NAME = "DeejNG";

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
#if DEBUG
                    Debug.WriteLine($"[Startup] Enabled using shortcut: {shortcutPath}");
#endif
                }
                else
                {
                    // Option 2: Use the current executable path as fallback
                    string exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                    if (File.Exists(exePath))
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                        key?.SetValue(APP_NAME, $"\"{exePath}\"", RegistryValueKind.String);
#if DEBUG
                        Debug.WriteLine($"[Startup] Enabled using executable: {exePath}");
#endif
                    }
                    else
                    {
                        throw new FileNotFoundException("Unable to locate application executable or shortcut for startup registration.");
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to enable startup: {ex.Message}");
#endif
                MessageBox.Show($"Failed to enable startup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void DisableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key?.DeleteValue(APP_NAME, false);
#if DEBUG
                Debug.WriteLine("[Startup] Disabled successfully");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to disable startup: {ex.Message}");
#endif
                MessageBox.Show($"Failed to disable startup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                var value = key?.GetValue(APP_NAME) as string;
                bool isEnabled = !string.IsNullOrEmpty(value);
#if DEBUG
                Debug.WriteLine($"[Startup] Startup is {(isEnabled ? "enabled" : "disabled")}");
#endif
                return isEnabled;
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to check startup status: {ex.Message}");
#endif
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
#if DEBUG
                    Debug.WriteLine("[Icon] Executable path not found, skipping icon setup");
#endif
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
#if DEBUG
                        Debug.WriteLine($"[Icon] Set display icon for {displayName}");
#endif
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Failed to set display icon: {ex.Message}");
#endif
                // Don't show message box for icon errors as they're not critical
            }
        }

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
#if DEBUG
                        Debug.WriteLine($"[Startup] Found shortcut at: {path}");
#endif
                        return path;
                    }
                }

#if DEBUG
                Debug.WriteLine("[Startup] No shortcut found in any expected location");
                Debug.WriteLine("[Startup] Checked paths:");
                foreach (var path in possiblePaths)
                {
                    Debug.WriteLine($"  - {path}");
                }
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[ERROR] Error searching for application shortcut: {ex.Message}");
#endif
            }

            return string.Empty;
        }
    }
}
