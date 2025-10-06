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
    /// Handles persistent storage of overlay position with multi-monitor support
    /// and aggressive save strategy to prevent data loss
    /// </summary>
    public class OverlayPositionPersistenceService : IDisposable
    {
        private readonly string _positionFilePath;
        private readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);
        private DateTime _lastSaveTime = DateTime.MinValue;
        private CancellationTokenSource _debounceCts;
        private const int DEBOUNCE_MS = 300; // Shorter debounce for more responsive saves
        private const int FORCE_SAVE_INTERVAL_MS = 5000; // Force save every 5 seconds if dirty

        private OverlayPositionData _currentPosition;
        private bool _isDirty = false;

        public event EventHandler<OverlayPositionData> PositionLoaded;

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
#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] Created/verified directory: {appDataFolder}");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] ERROR creating directory: {ex.Message}");
#endif
            }
            
            _positionFilePath = Path.Combine(appDataFolder, "overlay_position.json");

#if DEBUG
            Debug.WriteLine($"[OverlayPersistence] Using position file: {_positionFilePath}");
            Debug.WriteLine(ScreenPositionManager.GetScreenDiagnostics());
#endif
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
#if DEBUG
                    Debug.WriteLine("[OverlayPersistence] No saved position file found");
#endif
                    return GetDefaultPosition();
                }

                var json = File.ReadAllText(_positionFilePath);
#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] Raw JSON: {json}");
#endif
                
                var position = JsonSerializer.Deserialize<OverlayPositionData>(json);

                if (position == null)
                {
#if DEBUG
                    Debug.WriteLine("[OverlayPersistence] Deserialized position is null");
#endif
                    return GetDefaultPosition();
                }

                // Validate and correct position for current display configuration
                double correctedX, correctedY;
                bool needsCorrection = ValidateAndCorrectForDisplayChanges(position, out correctedX, out correctedY);

                if (needsCorrection)
                {
#if DEBUG
                    Debug.WriteLine($"[OverlayPersistence] Position corrected: ({position.X}, {position.Y}) -> ({correctedX}, {correctedY})");
#endif
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
#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] Loaded position: X={position.X}, Y={position.Y}, Screen={position.ScreenDeviceName}");
#endif

                PositionLoaded?.Invoke(this, position);
                return position;
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] Error loading position: {ex.Message}");
#endif
                return GetDefaultPosition();
            }
        }

        /// <summary>
        /// Save overlay position with debouncing and screen tracking
        /// </summary>
        public void SavePosition(double x, double y)
        {
#if DEBUG
            Debug.WriteLine($"[OverlayPersistence] SavePosition called: X={x}, Y={y}");
#endif
            
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
#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] Position unchanged, skipping save");
#endif
                return; // No change, skip save
            }

            _currentPosition = newPosition;
            _isDirty = true;

#if DEBUG
            Debug.WriteLine($"[OverlayPersistence] Position marked dirty, scheduling save");
            Debug.WriteLine($"[OverlayPersistence] Screen: {screenInfo.DeviceName}, Bounds: {screenInfo.Bounds}");
#endif

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
#if DEBUG
                        Debug.WriteLine("[OverlayPersistence] Debounce complete, executing save");
#endif
                        await SaveToFileAsync(newPosition);
                    }
                    else
                    {
#if DEBUG
                        Debug.WriteLine("[OverlayPersistence] Debounced save cancelled");
#endif
                    }
                }, TaskContinuationOptions.OnlyOnRanToCompletion);

#if DEBUG
            Debug.WriteLine($"[OverlayPersistence] Position queued for save: X={x}, Y={y}");
#endif
        }

        /// <summary>
        /// Force immediate save (called on app shutdown)
        /// </summary>
        public async Task ForceSaveAsync()
        {
            if (_currentPosition != null && _isDirty)
            {
#if DEBUG
                Debug.WriteLine("[OverlayPersistence] Force saving position");
#endif
                await SaveToFileAsync(_currentPosition);
            }
        }

        /// <summary>
        /// Periodically force save if position is dirty (called from timer)
        /// </summary>
        public async Task PeriodicSaveAsync()
        {
            if (_isDirty && (DateTime.Now - _lastSaveTime).TotalMilliseconds > FORCE_SAVE_INTERVAL_MS)
            {
#if DEBUG
                Debug.WriteLine("[OverlayPersistence] Periodic force save");
#endif
                if (_currentPosition != null)
                {
                    await SaveToFileAsync(_currentPosition);
                }
            }
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

#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] Position saved to disk:");
                Debug.WriteLine($"  X={position.X}, Y={position.Y}");
                Debug.WriteLine($"  Screen={position.ScreenDeviceName}");
                Debug.WriteLine($"  Bounds={position.ScreenBounds}");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] Error saving position: {ex.Message}");
#endif
            }
            finally
            {
                _saveLock.Release();
            }
        }

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
#if DEBUG
                Debug.WriteLine("[OverlayPersistence] No screen info in saved position, using basic validation");
#endif
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
#if DEBUG
                    Debug.WriteLine("[OverlayPersistence] Screen found with same bounds, position is valid");
#endif
                    return false;
                }
                else
                {
                    // Screen exists but bounds changed - adjust proportionally
#if DEBUG
                    Debug.WriteLine($"[OverlayPersistence] Screen bounds changed: {position.ScreenBounds} -> {currentBounds}");
#endif
                    
                    var oldBounds = ParseBounds(position.ScreenBounds);
                    var newBounds = savedScreen.Bounds;

                    // Calculate relative position within old screen
                    double relativeX = (position.X - oldBounds.Left) / oldBounds.Width;
                    double relativeY = (position.Y - oldBounds.Top) / oldBounds.Height;

                    // Apply to new screen bounds
                    correctedX = newBounds.Left + (relativeX * newBounds.Width);
                    correctedY = newBounds.Top + (relativeY * newBounds.Height);

#if DEBUG
                    Debug.WriteLine($"[OverlayPersistence] Position adjusted for bounds change: ({position.X}, {position.Y}) -> ({correctedX}, {correctedY})");
#endif
                    return true;
                }
            }
            else
            {
                // Screen no longer exists - move to primary screen
#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] Screen '{position.ScreenDeviceName}' no longer exists, moving to primary");
#endif
                
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

#if DEBUG
                Debug.WriteLine($"[OverlayPersistence] Position moved to primary screen: ({correctedX}, {correctedY})");
#endif
                return true;
            }
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

        private string GetOperatingSystemVersion()
        {
            return $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}";
        }

        public void Dispose()
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _saveLock?.Dispose();

#if DEBUG
            Debug.WriteLine("[OverlayPersistence] Service disposed");
#endif
        }
    }

    /// <summary>
    /// Data class for storing overlay position with multi-monitor metadata
    /// </summary>
    public class OverlayPositionData
    {
        public double X { get; set; }
        public double Y { get; set; }
        public string ScreenDeviceName { get; set; }
        public string ScreenBounds { get; set; }
        public string OperatingSystem { get; set; }
        public DateTime SavedAt { get; set; }
    }
}
