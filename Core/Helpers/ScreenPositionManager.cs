using DeejNG.Classes;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Forms;

namespace DeejNG.Core.Helpers
{
    /// <summary>
    /// Manages overlay positioning across multiple monitors, handling display configuration changes
    /// </summary>
    public static class ScreenPositionManager
    {
        #region Public Methods

        /// <summary>
        /// Gets diagnostic information about current screen configuration
        /// </summary>
        public static string GetScreenDiagnostics()
        {
            var screens = Screen.AllScreens;
            var diagnostics = $"Screen Count: {screens.Length}\n";
            diagnostics += $"Virtual Screen: {SystemParameters.VirtualScreenLeft},{SystemParameters.VirtualScreenTop} {SystemParameters.VirtualScreenWidth}x{SystemParameters.VirtualScreenHeight}\n";

            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                diagnostics += $"Screen {i}: {screen.DeviceName} - {screen.Bounds} {(screen.Primary ? "(Primary)" : "")}\n";
            }

            return diagnostics;
        }

        /// <summary>
        /// Gets information about the screen at the specified coordinates
        /// </summary>
        public static ScreenInfo GetScreenInfo(double x, double y)
        {
            var point = new System.Drawing.Point((int)x, (int)y);
            var screen = Screen.FromPoint(point);

            return new ScreenInfo
            {
                DeviceName = screen.DeviceName,
                Bounds = $"{screen.Bounds.Left},{screen.Bounds.Top},{screen.Bounds.Width},{screen.Bounds.Height}",
                WorkingArea = screen.WorkingArea,
                IsPrimary = screen.Primary
            };
        }

        /// <summary>
        /// Validates if a saved position is still valid for the current display configuration
        /// and corrects it if needed
        /// </summary>
        public static bool ValidateAndCorrectPosition(AppSettings settings, out double correctedX, out double correctedY)
        {
            correctedX = settings.OverlayX;
            correctedY = settings.OverlayY;

            // If no screen information was saved, validate against virtual screen bounds
            if (string.IsNullOrEmpty(settings.OverlayScreenDevice))
            {
                return ValidateAgainstVirtualScreen(settings.OverlayX, settings.OverlayY, out correctedX, out correctedY);
            }

            // Try to find the screen that was saved
            var savedScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.OverlayScreenDevice);

            if (savedScreen != null)
            {
                // Screen exists - check if bounds have changed
                var currentBounds = $"{savedScreen.Bounds.Left},{savedScreen.Bounds.Top},{savedScreen.Bounds.Width},{savedScreen.Bounds.Height}";

                if (currentBounds == settings.OverlayScreenBounds)
                {
                    // Screen exists with same bounds - position is valid

                    return false; // No correction needed
                }
                else
                {
                    // Screen exists but bounds changed - adjust position proportionally

                    var oldBounds = ParseBounds(settings.OverlayScreenBounds);
                    var newBounds = savedScreen.Bounds;

                    // Calculate relative position within old screen
                    double relativeX = (settings.OverlayX - oldBounds.Left) / oldBounds.Width;
                    double relativeY = (settings.OverlayY - oldBounds.Top) / oldBounds.Height;

                    // Apply to new screen bounds
                    correctedX = newBounds.Left + (relativeX * newBounds.Width);
                    correctedY = newBounds.Top + (relativeY * newBounds.Height);

                    return true; // Correction applied
                }
            }
            else
            {
                // Screen no longer exists - move to primary screen

                var primaryScreen = Screen.PrimaryScreen;
                var oldBounds = ParseBounds(settings.OverlayScreenBounds);

                // Calculate relative position within old screen
                double relativeX = (settings.OverlayX - oldBounds.Left) / oldBounds.Width;
                double relativeY = (settings.OverlayY - oldBounds.Top) / oldBounds.Height;

                // Apply to primary screen, clamped to safe area
                correctedX = primaryScreen.Bounds.Left + (relativeX * primaryScreen.Bounds.Width);
                correctedY = primaryScreen.Bounds.Top + (relativeY * primaryScreen.Bounds.Height);

                // Ensure it's within visible bounds
                correctedX = Math.Max(primaryScreen.Bounds.Left, Math.Min(correctedX, primaryScreen.Bounds.Right - 200));
                correctedY = Math.Max(primaryScreen.Bounds.Top, Math.Min(correctedY, primaryScreen.Bounds.Bottom - 100));

                return true; // Correction applied
            }
        }

        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Parses bounds string in format "Left,Top,Width,Height"
        /// </summary>
        private static System.Drawing.Rectangle ParseBounds(string boundsString)
        {
            if (string.IsNullOrEmpty(boundsString))
                return System.Drawing.Rectangle.Empty;

            try
            {
                var parts = boundsString.Split(',');
                if (parts.Length == 4)
                {
                    return new System.Drawing.Rectangle(
                        int.Parse(parts[0]),  // Left
                        int.Parse(parts[1]),  // Top
                        int.Parse(parts[2]),  // Width
                        int.Parse(parts[3])   // Height
                    );
                }
            }
            catch (Exception ex)
            {
            }

            return System.Drawing.Rectangle.Empty;
        }

        /// <summary>
        /// Validates position against virtual screen bounds and corrects if needed
        /// </summary>
        private static bool ValidateAgainstVirtualScreen(double x, double y, out double correctedX, out double correctedY)
        {
            correctedX = x;
            correctedY = y;

            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            const double margin = 100; // Allow some margin for edge cases

            bool needsCorrection = false;

            if (x < virtualLeft - margin)
            {
                correctedX = virtualLeft + 50;
                needsCorrection = true;
            }
            else if (x > virtualRight - 50)
            {
                correctedX = virtualRight - 200;
                needsCorrection = true;
            }

            if (y < virtualTop - margin)
            {
                correctedY = virtualTop + 50;
                needsCorrection = true;
            }
            else if (y > virtualBottom - 50)
            {
                correctedY = virtualBottom - 100;
                needsCorrection = true;
            }

            return needsCorrection;
        }

        #endregion Private Methods
    }

    /// <summary>
    /// Information about a screen/monitor
    /// </summary>
    public class ScreenInfo
    {
        #region Public Properties

        public string Bounds { get; set; }
        public string DeviceName { get; set; }
        public bool IsPrimary { get; set; }
        public System.Drawing.Rectangle WorkingArea { get; set; }

        #endregion Public Properties
    }
}