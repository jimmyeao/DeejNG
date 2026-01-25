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
        #region Private Fields

        private readonly object _overlayLock = new object();
        private bool _disposed = false;
        private List<string> _lastLabels = new();
        private List<float> _lastVolumes = new();
        private DateTime _lastVolumeUpdate = DateTime.MinValue;
        private FloatingOverlay _overlay;
        private MainWindow _parentWindow;

        #endregion Private Fields

        #region Public Events

        public event EventHandler OverlayHidden;

        // Events
        public event EventHandler<OverlayPositionChangedEventArgs> PositionChanged;

        #endregion Public Events

        #region Public Properties

        // Properties
        public bool IsEnabled { get; set; } = true;
        public bool IsVisible => _overlay?.IsVisible == true;
        public double Opacity { get; set; } = 0.9;
        public string TextColorMode { get; set; } = "Auto";
        public int TimeoutSeconds { get; set; } = 2;
        public double X { get; set; } = 100;
        public double Y { get; set; } = 100;

        #endregion Public Properties

        #region Public Methods

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

            }
        }

        public void ForceHide()
        {
            IsEnabled = false;
            HideOverlay();
        }

        public void HideOverlay()
        {
            lock (_overlayLock)
            {
                if (_overlay?.IsVisible == true)
                {
                    _overlay.Hide();
                    OverlayHidden?.Invoke(this, EventArgs.Empty);

                }
            }
        }

        public void Initialize()
        {
            // Find the main window but don't depend on its state
            _parentWindow = Application.Current?.MainWindow as MainWindow;

        }

        public void ShowOverlay(List<float> volumes, List<string> labels)
        {
            if (!IsEnabled)
            {

                return;
            }

            // Throttle updates for performance
            if (DateTime.Now.Subtract(_lastVolumeUpdate).TotalMilliseconds < 16) // ~60fps
            {
                return;
            }
            _lastVolumeUpdate = DateTime.Now;

            // Skip if nothing materially changed to reduce draw workload
            // BUT: Don't skip if overlay is currently hidden - we need to re-show it
            if (IsSame(volumes, _lastVolumes) && IsSame(labels, _lastLabels) && IsVisible)
            {
                return;
            }
            _lastVolumes = new List<float>(volumes);
            _lastLabels = labels != null ? new List<string>(labels) : new List<string>();

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

                }
            }



            PositionChanged?.Invoke(this, new OverlayPositionChangedEventArgs { X = X, Y = Y });


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

                    }
                    else
                    {

                    }
                }
                else
                {

                }
            }


        }

        #endregion Public Methods

        #region Private Methods

        private void EnsureOverlayExists()
        {
            if (_overlay == null)
            {


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

                }
                else
                {

                }

                _overlay = new FloatingOverlay(settings, parentWindow);

                // Wire up position change events
                _overlay.LocationChanged += OnOverlayLocationChanged;
                _overlay.OverlayPositionChanged += OnOverlayPositionChangedEvent;

                // Position is applied in FloatingOverlay.OnSourceInitialized - don't apply it here
                // to avoid timing conflicts that cause position to be lost


            }
        }

        private List<string> GenerateDefaultLabels(int count)
        {
            return Enumerable.Range(1, count).Select(i => $"Ch {i}").ToList();
        }

        private bool IsSame(List<float> a, List<float> b)
        {
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                float diff = Math.Abs(a[i] - b[i]);

                // BUGFIX for issue #97: Allow extremes (0% and 100%) to always update
                // This prevents smoothing from causing values to get stuck at 1% or 99%
                bool isAtExtreme = (a[i] <= 0.01f || a[i] >= 0.99f) || (b[i] <= 0.01f || b[i] >= 0.99f);

                if (isAtExtreme)
                {
                    // Use tighter tolerance near extremes to ensure 0% and 100% are shown
                    if (diff > 0.001f) return false; // 0.1% tolerance at extremes
                }
                else
                {
                    // Normal tolerance for mid-range values (performance optimization)
                    if (diff > 0.005f) return false; // 0.5% tolerance
                }
            }
            return true;
        }

        private bool IsSame(List<string> a, List<string> b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            }
            return true;
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

        private void OnOverlayLocationChanged(object sender, EventArgs e)
        {
            if (_overlay != null)
            {
                UpdatePosition(_overlay.Left, _overlay.Top);
            }
        }

        private void OnOverlayPositionChangedEvent(object sender, DeejNG.Views.OverlayPositionEventArgs e)
        {

            UpdatePosition(e.X, e.Y);
        }

        #endregion Private Methods
    }
}
