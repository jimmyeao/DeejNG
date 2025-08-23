using DeejNG;
using DeejNG.Classes;
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
            Debug.WriteLine("[OverlayService] Initialized");
        }

        public void ShowOverlay(List<float> volumes, List<string> labels)
        {
            if (!IsEnabled)
            {
                Debug.WriteLine("[OverlayService] Overlay disabled");
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
                    Debug.WriteLine("[OverlayService] Overlay hidden");
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
                    Debug.WriteLine($"[OverlayService] Applied position to existing overlay: ({X}, {Y})");
                }
            }

            PositionChanged?.Invoke(this, new OverlayPositionChangedEventArgs { X = X, Y = Y });
            Debug.WriteLine($"[OverlayService] Position updated: X={X}, Y={Y}");
        }

        public void UpdateSettings(AppSettings settings)
        {
            IsEnabled = settings.OverlayEnabled;
            Opacity = settings.OverlayOpacity;
            TimeoutSeconds = settings.OverlayTimeoutSeconds;
            TextColorMode = settings.OverlayTextColor ?? "Auto";
            
            // Always update position from settings (don't validate, let FloatingOverlay handle clamping)
            X = settings.OverlayX;
            Y = settings.OverlayY;
            
            Debug.WriteLine($"[OverlayService] Position loaded from settings: X={X}, Y={Y}");

            lock (_overlayLock)
            {
                if (_overlay != null)
                {
                    _overlay.UpdateSettings(settings);
                }
            }

            Debug.WriteLine($"[OverlayService] Settings updated - Enabled: {IsEnabled}, TextColor: {TextColorMode}, Position: ({X}, {Y})");
        }

        private void EnsureOverlayExists()
        {
            if (_overlay == null)
            {
                Debug.WriteLine($"[OverlayService] Creating new overlay instance at position ({X}, {Y})");

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
                    Debug.WriteLine("[OverlayService] Using MainWindow as parent (visible)");
                }
                else
                {
                    Debug.WriteLine("[OverlayService] Creating standalone overlay (MainWindow hidden/minimized)");
                }

                _overlay = new FloatingOverlay(settings, parentWindow);
                
                // POSITION FIX: Ensure position is applied after creation
                // Apply saved position unless both X and Y are the default (100, 100)
                if (!(X == 100 && Y == 100))
                {
                    _overlay.Left = X;
                    _overlay.Top = Y;
                    Debug.WriteLine($"[OverlayService] Applied saved position to overlay: ({X}, {Y})");
                }
                else
                {
                    Debug.WriteLine($"[OverlayService] Using overlay default position (no saved position)");
                }
                
                // Wire up position change events
                _overlay.LocationChanged += OnOverlayLocationChanged;
                
                Debug.WriteLine($"[OverlayService] Overlay created with parent: {(parentWindow != null ? "MainWindow" : "standalone")}");
            }
        }

        private void OnOverlayLocationChanged(object sender, EventArgs e)
        {
            if (_overlay != null)
            {
                UpdatePosition(_overlay.Left, _overlay.Top);
            }
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
                        _overlay.Close();
                        _overlay = null;
                    }
                }
                _disposed = true;
                Debug.WriteLine("[OverlayService] Disposed");
            }
        }
    }
}
