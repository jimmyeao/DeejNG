using DeejNG.Models;
using DeejNG.Core.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DeejNG.Core.Services
{
    /// <summary>
    /// Data class for storing overlay position with multi-monitor metadata
    /// </summary>
    public class OverlayPositionData
    {
        #region Public Properties

        public string OperatingSystem { get; set; }
        public DateTime SavedAt { get; set; }
        public string ScreenBounds { get; set; }
        public string ScreenDeviceName { get; set; }
        public double X { get; set; }
        public double Y { get; set; }

        #endregion Public Properties
    }

    /// <summary>
    /// Handles persistent storage of overlay position with multi-monitor support
    /// and aggressive save strategy to prevent data loss
    /// </summary>
    public class OverlayPositionPersistenceService : IDisposable
    {
        #region Private Fields

        private const int DEBOUNCE_MS = 300;
        // Shorter debounce for more responsive saves
        private const int FORCE_SAVE_INTERVAL_MS = 5000;

        private readonly string _positionFilePath;
        private readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);
        private OverlayPositionData _currentPosition;
        private CancellationTokenSource _debounceCts;
        // Force save every 5 seconds if dirty
        private bool _isDirty = false;

        private DateTime _lastSaveTime = DateTime.MinValue;

        #endregion Private Fields

        #region Public Constructors

        public OverlayPositionPersistenceService()
        {
            // Store position file in user's AppData to avoid permission issues
            var appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeejNG"
            );

            try
            {
                Directory.CreateDirectory(appDataFolder);

            }
            catch (Exception ex)
            {

            }

            _positionFilePath = Path.Combine(appDataFolder, "overlay_position.json");


        }

        #endregion Public Constructors

        #region Public Events

        public event EventHandler<OverlayPositionData> PositionLoaded;

        #endregion Public Events

        #region Public Methods

        public void Dispose()
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _saveLock?.Dispose();


        }

        /// <summary>
        /// Force immediate save (called on app shutdown)
        /// </summary>
        public async Task ForceSaveAsync()
        {
            if (_currentPosition != null && _isDirty)
            {

                await SaveToFileAsync(_currentPosition);
            }
        }

        /// <summary>
        /// Load the saved overlay position, with multi-monitor validation
        /// </summary>
        public OverlayPositionData LoadPosition()
        {
            try
            {
                if (!File.Exists(_positionFilePath))
                {

                    return GetDefaultPosition();
                }

                var json = File.ReadAllText(_positionFilePath);

                
                var position = JsonSerializer.Deserialize<OverlayPositionData>(json);

                if (position == null)
                {

                    return GetDefaultPosition();
                }

                // Validate and correct position for current display configuration
                double correctedX, correctedY;
                bool needsCorrection = ValidateAndCorrectForDisplayChanges(position, out correctedX, out correctedY);

                if (needsCorrection)
                {

                    position.X = correctedX;
                    position.Y = correctedY;
                    
                    // Update screen info for corrected position
                    var screenInfo = ScreenPositionManager.GetScreenInfo(correctedX, correctedY);
                    position.ScreenDeviceName = screenInfo.DeviceName;
                    position.ScreenBounds = screenInfo.Bounds;
                    
                    // Mark as dirty so corrected position gets saved
                    _isDirty = true;
                }

                _currentPosition = position;


                PositionLoaded?.Invoke(this, position);
                return position;
            }
            catch (Exception ex)
            {

                return GetDefaultPosition();
            }
        }

        /// <summary>
        /// Periodically force save if position is dirty (called from timer)
        /// </summary>
        public async Task PeriodicSaveAsync()
        {
            if (_isDirty && (DateTime.Now - _lastSaveTime).TotalMilliseconds > FORCE_SAVE_INTERVAL_MS)
            {

                if (_currentPosition != null)
                {
                    await SaveToFileAsync(_currentPosition);
                }
            }
        }

        /// <summary>
        /// Save overlay position with debouncing and screen tracking
        /// </summary>
        public void SavePosition(double x, double y)
        {

            
            // Get screen information for this position
            var screenInfo = ScreenPositionManager.GetScreenInfo(x, y);
            
            var newPosition = new OverlayPositionData
            {
                X = Math.Round(x, 1),
                Y = Math.Round(y, 1),
                ScreenDeviceName = screenInfo.DeviceName,
                ScreenBounds = screenInfo.Bounds,
                OperatingSystem = GetOperatingSystemVersion(),
                SavedAt = DateTime.Now
            };

            // Check if position actually changed
            if (_currentPosition != null && 
                Math.Abs(_currentPosition.X - newPosition.X) < 0.1 && 
                Math.Abs(_currentPosition.Y - newPosition.Y) < 0.1 &&
                _currentPosition.ScreenDeviceName == newPosition.ScreenDeviceName)
            {

                return; // No change, skip save
            }

            _currentPosition = newPosition;
            _isDirty = true;


            // Cancel any pending debounced save
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            // Debounced save
            Task.Delay(DEBOUNCE_MS, token)
                .ContinueWith(async _ =>
                {
                    if (!token.IsCancellationRequested)
                    {

                        await SaveToFileAsync(newPosition);
                    }
                    else
                    {

                    }
                }, TaskContinuationOptions.OnlyOnRanToCompletion);


        }

        #endregion Public Methods

        #region Private Methods

        private OverlayPositionData GetDefaultPosition()
        {
            // Get primary screen and place overlay on the right side
            var primaryScreen = System.Windows.Forms.Screen.PrimaryScreen;
            var screenInfo = ScreenPositionManager.GetScreenInfo(primaryScreen.Bounds.Right - 350, primaryScreen.Bounds.Top + primaryScreen.Bounds.Height / 2);

            return new OverlayPositionData
            {
                X = primaryScreen.Bounds.Right - 350, // 350px from right edge
                Y = primaryScreen.Bounds.Top + (primaryScreen.Bounds.Height / 2) - 100, // Vertically centered
                ScreenDeviceName = screenInfo.DeviceName,
                ScreenBounds = screenInfo.Bounds,
                OperatingSystem = GetOperatingSystemVersion(),
                SavedAt = DateTime.Now
            };
        }

        private string GetOperatingSystemVersion()
        {
            return $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}";
        }

        private bool IsPositionInvalidForVirtualScreen(double x, double y, out double correctedX, out double correctedY)
        {
            correctedX = x;
            correctedY = y;

            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            const double margin = 100;
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

        private System.Drawing.Rectangle ParseBounds(string boundsString)
        {
            if (string.IsNullOrEmpty(boundsString))
                return System.Drawing.Rectangle.Empty;

            try
            {
                var parts = boundsString.Split(',');
                if (parts.Length == 4)
                {
                    return new System.Drawing.Rectangle(
                        int.Parse(parts[0]),
                        int.Parse(parts[1]),
                        int.Parse(parts[2]),
                        int.Parse(parts[3])
                    );
                }
            }
            catch
            {
                // Ignore parse errors
            }

            return System.Drawing.Rectangle.Empty;
        }

        private async Task SaveToFileAsync(OverlayPositionData position)
        {
            await _saveLock.WaitAsync();
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(position, options);
                
                // Write to temp file first, then move (atomic operation)
                var tempFile = _positionFilePath + ".tmp";
                await File.WriteAllTextAsync(tempFile, json);
                File.Move(tempFile, _positionFilePath, true);
                
                _lastSaveTime = DateTime.Now;
                _isDirty = false;


            }
            catch (Exception ex)
            {

            }
            finally
            {
                _saveLock.Release();
            }
        }
        /// <summary>
        /// Validates saved position and corrects it if display configuration changed
        /// </summary>
        private bool ValidateAndCorrectForDisplayChanges(OverlayPositionData position, out double correctedX, out double correctedY)
        {
            correctedX = position.X;
            correctedY = position.Y;

            // If no screen information was saved, just validate against virtual screen
            if (string.IsNullOrEmpty(position.ScreenDeviceName))
            {

                return IsPositionInvalidForVirtualScreen(position.X, position.Y, out correctedX, out correctedY);
            }

            // Try to find the saved screen
            var allScreens = System.Windows.Forms.Screen.AllScreens;
            var savedScreen = Array.Find(allScreens, s => s.DeviceName == position.ScreenDeviceName);

            if (savedScreen != null)
            {
                // Screen still exists - check if bounds changed
                var currentBounds = $"{savedScreen.Bounds.Left},{savedScreen.Bounds.Top},{savedScreen.Bounds.Width},{savedScreen.Bounds.Height}";
                
                if (currentBounds == position.ScreenBounds)
                {
                    // Same screen, same bounds - position is valid

                    return false;
                }
                else
                {
                    // Screen exists but bounds changed - adjust proportionally

                    
                    var oldBounds = ParseBounds(position.ScreenBounds);
                    var newBounds = savedScreen.Bounds;

                    // Calculate relative position within old screen
                    double relativeX = (position.X - oldBounds.Left) / oldBounds.Width;
                    double relativeY = (position.Y - oldBounds.Top) / oldBounds.Height;

                    // Apply to new screen bounds
                    correctedX = newBounds.Left + (relativeX * newBounds.Width);
                    correctedY = newBounds.Top + (relativeY * newBounds.Height);


                    return true;
                }
            }
            else
            {
                // Screen no longer exists - move to primary screen

                
                var primaryScreen = System.Windows.Forms.Screen.PrimaryScreen;
                var oldBounds = ParseBounds(position.ScreenBounds);

                if (!oldBounds.IsEmpty)
                {
                    // Calculate relative position within old screen
                    double relativeX = (position.X - oldBounds.Left) / oldBounds.Width;
                    double relativeY = (position.Y - oldBounds.Top) / oldBounds.Height;

                    // Apply to primary screen
                    correctedX = primaryScreen.Bounds.Left + (relativeX * primaryScreen.Bounds.Width);
                    correctedY = primaryScreen.Bounds.Top + (relativeY * primaryScreen.Bounds.Height);
                }
                else
                {
                    // Can't parse old bounds, just center on primary
                    correctedX = primaryScreen.Bounds.Right - 350;
                    correctedY = primaryScreen.Bounds.Top + (primaryScreen.Bounds.Height / 2);
                }


                return true;
            }
        }

        #endregion Private Methods
    }
}
