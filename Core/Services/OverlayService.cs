using DeejNG;
using DeejNG.Classes;
using DeejNG.Core.Helpers;
using DeejNG.Core.Interfaces;
using DeejNG.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace DeejNG.Core.Services
{
    public class OverlayService : IOverlayService, IDisposable
    {
        private FloatingOverlay _overlay;
        private readonly object _overlayLock = new object();
        private DateTime _lastVolumeUpdate = DateTime.MinValue;
        private bool _disposed = false;
        private MainWindow _parentWindow;
        
        // Properties
        public bool IsEnabled { get; set; } = true;
        public bool IsVisible => _overlay?.IsVisible == true;
        public double Opacity { get; set; } = 0.9;
        public int TimeoutSeconds { get; set; } = 2;
        public string TextColorMode { get; set; } = "Auto";
        public double X { get; set; } = 100;
        public double Y { get; set; } = 100;

        // Events
        public event EventHandler<OverlayPositionChangedEventArgs> PositionChanged;
        public event EventHandler OverlayHidden;

        public void Initialize()
        {
            // Find the main window but don't depend on its state
            _parentWindow = Application.Current?.MainWindow as MainWindow;
#if DEBUG
            Debug.WriteLine("[OverlayService] Initialized");
#endif
        }

        public void ShowOverlay(List<float> volumes, List<string> labels)
        {
            if (!IsEnabled)
            {
#if DEBUG
                Debug.WriteLine("[OverlayService] Overlay disabled");
#endif
                return;
            }

            // Throttle updates for performance
            if (DateTime.Now.Subtract(_lastVolumeUpdate).TotalMilliseconds < 16) // ~60fps
            {
                return;
            }
            _lastVolumeUpdate = DateTime.Now;

            // Ensure we're on the UI thread
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => ShowOverlay(volumes, labels));
                return;
            }

            lock (_overlayLock)
            {
                EnsureOverlayExists();
                
                if (_overlay != null)
                {
                    _overlay.ShowVolumes(volumes, labels ?? GenerateDefaultLabels(volumes.Count));
                }
            }
        }

        public void HideOverlay()
        {
            lock (_overlayLock)
            {
                if (_overlay?.IsVisible == true)
                {
                    _overlay.Hide();
                    OverlayHidden?.Invoke(this, EventArgs.Empty);
#if DEBUG
                    Debug.WriteLine("[OverlayService] Overlay hidden");
#endif
                }
            }
        }

        public void ForceHide()
        {
            IsEnabled = false;
            HideOverlay();
        }

        public void UpdatePosition(double x, double y)
        {
            X = Math.Round(x, 1);
            Y = Math.Round(y, 1);
            
            lock (_overlayLock)
            {
                if (_overlay != null)
                {
                    // Apply position immediately to existing overlay
                    _overlay.Left = X;
                    _overlay.Top = Y;
#if DEBUG
                    Debug.WriteLine($"[OverlayService] Applied position to existing overlay: ({X}, {Y})");
#endif
                }
            }

#if DEBUG
            var subscriberCount = PositionChanged?.GetInvocationList()?.Length ?? 0;
            Debug.WriteLine($"[OverlayService] About to fire PositionChanged event, subscriber count: {subscriberCount}");
#endif
            
            PositionChanged?.Invoke(this, new OverlayPositionChangedEventArgs { X = X, Y = Y });
            
#if DEBUG
            Debug.WriteLine($"[OverlayService] Position updated: X={X}, Y={Y}");
#endif
        }

        public void UpdateSettings(AppSettings settings)
        {
            IsEnabled = settings.OverlayEnabled;
            Opacity = settings.OverlayOpacity;
            TimeoutSeconds = settings.OverlayTimeoutSeconds;
            TextColorMode = settings.OverlayTextColor ?? "Auto";
            
            // MULTI-MONITOR FIX: Validate and correct position for current display configuration
            double validatedX, validatedY;
            bool positionCorrected = ScreenPositionManager.ValidateAndCorrectPosition(settings, out validatedX, out validatedY);
            
            X = validatedX;
            Y = validatedY;
            
#if DEBUG
            if (positionCorrected)
            {
                Debug.WriteLine($"[OverlayService] Position corrected for display changes: ({settings.OverlayX}, {settings.OverlayY}) -> ({X}, {Y})");
                Debug.WriteLine(ScreenPositionManager.GetScreenDiagnostics());
            }
            else
            {
                Debug.WriteLine($"[OverlayService] Position validated successfully: X={X}, Y={Y}");
            }
#endif

            lock (_overlayLock)
            {
                if (_overlay != null)
                {
                    _overlay.UpdateSettings(settings);
                    
                    // CRITICAL: Only apply position if window is fully loaded
                    // Don't interfere with OnSourceInitialized position application
                    if (_overlay.IsLoaded)
                    {
                        _overlay.Left = X;
                        _overlay.Top = Y;
#if DEBUG
                        Debug.WriteLine($"[OverlayService] Applied validated position to loaded overlay: ({X}, {Y})");
#endif
                    }
                    else
                    {
#if DEBUG
                        Debug.WriteLine($"[OverlayService] Skipping position application - overlay not loaded yet (will be applied in OnSourceInitialized)");
#endif
                    }
                }
                else
                {
#if DEBUG
                    Debug.WriteLine($"[OverlayService] Overlay not created yet - position will be applied when created: ({X}, {Y})");
#endif
                }
            }

#if DEBUG
            Debug.WriteLine($"[OverlayService] Settings updated - Enabled: {IsEnabled}, TextColor: {TextColorMode}, Position: ({X}, {Y})");
#endif
        }

        private void EnsureOverlayExists()
        {
            if (_overlay == null)
            {
#if DEBUG
                Debug.WriteLine($"[OverlayService] Creating new overlay instance at position ({X}, {Y})");
#endif

                // Create settings object with current service values
                var settings = new AppSettings
                {
                    OverlayEnabled = IsEnabled,
                    OverlayOpacity = Opacity,
                    OverlayTimeoutSeconds = TimeoutSeconds,
                    OverlayTextColor = TextColorMode,
                    OverlayX = X,
                    OverlayY = Y
                };

                // CRITICAL FIX: Determine parent window based on visibility
                MainWindow parentWindow = null;
                if (_parentWindow?.IsVisible == true && _parentWindow.WindowState != WindowState.Minimized)
                {
                    parentWindow = _parentWindow;
#if DEBUG
                    Debug.WriteLine("[OverlayService] Using MainWindow as parent (visible)");
#endif
                }
                else
                {
#if DEBUG
                    Debug.WriteLine("[OverlayService] Creating standalone overlay (MainWindow hidden/minimized)");
#endif
                }

                _overlay = new FloatingOverlay(settings, parentWindow);
                
                // Wire up position change events
                _overlay.LocationChanged += OnOverlayLocationChanged;
                _overlay.OverlayPositionChanged += OnOverlayPositionChangedEvent;
                
                // Position is applied in FloatingOverlay.OnSourceInitialized - don't apply it here
                // to avoid timing conflicts that cause position to be lost
                
#if DEBUG
                Debug.WriteLine($"[OverlayService] Overlay created with parent: {(parentWindow != null ? "MainWindow" : "standalone")}");
#endif
            }
        }

        private void OnOverlayLocationChanged(object sender, EventArgs e)
        {
            if (_overlay != null)
            {
                UpdatePosition(_overlay.Left, _overlay.Top);
            }
        }

        private void OnOverlayPositionChangedEvent(object sender, DeejNG.Views.OverlayPositionEventArgs e)
        {
#if DEBUG
            Debug.WriteLine($"[OverlayService] Received OverlayPositionChanged event: X={e.X}, Y={e.Y}");
#endif
            UpdatePosition(e.X, e.Y);
        }

        private List<string> GenerateDefaultLabels(int count)
        {
            return Enumerable.Range(1, count).Select(i => $"Ch {i}").ToList();
        }

        private bool IsValidPosition(double x, double y)
        {
            // More permissive validation - allow positions anywhere within expanded virtual screen bounds
            var virtualLeft = SystemParameters.VirtualScreenLeft - 300; // Allow offscreen positioning
            var virtualTop = SystemParameters.VirtualScreenTop - 200;
            var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 300;
            var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 200;
            
            return x >= virtualLeft && x <= virtualRight && y >= virtualTop && y <= virtualBottom;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                lock (_overlayLock)
                {
                    if (_overlay != null)
                    {
                        _overlay.LocationChanged -= OnOverlayLocationChanged;
                        _overlay.OverlayPositionChanged -= OnOverlayPositionChangedEvent;
                        _overlay.Close();
                        _overlay = null;
                    }
                }
                _disposed = true;
#if DEBUG
                Debug.WriteLine("[OverlayService] Disposed");
#endif
            }
        }
    }
}
