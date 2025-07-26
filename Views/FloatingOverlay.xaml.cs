// Updated FloatingOverlay.xaml.cs with proper text color handling

using DeejNG.Classes;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeejNG.Views
{
    public partial class FloatingOverlay : Window
    {
        #region Private Fields
        // Win32 API constants

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int HWND_TOPMOST = -1;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const float HorizontalSpacing = 130f;
        private const float LabelOffset = 65f;
        private const int MaxChannelsPerRow = 6;
        // Layout constants
        private const float MeterSize = 90f;
        private const float Padding = 25f;
        private const float VerticalSpacing = 125f;
        private readonly DispatcherTimer _hideTimer = new();
        private DispatcherTimer? _autoCloseTimer;
        private DispatcherTimer _backgroundAnalysisTimer;
        private List<string> _channelLabels = new();
        private bool _isDragging = false;
        private bool _isUpdatingSettings = false;
        private bool _isWhiteTextOptimal = true;
        private DateTime _lastVolumeUpdate = DateTime.MinValue;
        private MainWindow _parentWindow;
        private string _textColorMode = "Auto";
        private List<float> _volumes = new();
        // Win32 API imports
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        #endregion Private Fields

        #region Public Constructors

        public FloatingOverlay(AppSettings settings, MainWindow parentWindow = null)
        {
            InitializeComponent();

            // Essential focus prevention (minimal overhead)
            this.ShowActivated = false;
            this.Focusable = false;
            this.IsTabStop = false;
            this.Topmost = true;
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Background = System.Windows.Media.Brushes.Transparent;
            this.ResizeMode = ResizeMode.NoResize;
            this.ShowInTaskbar = false;
            this.Owner = parentWindow;

            SetPrecisePosition(settings.OverlayX, settings.OverlayY);
            OverlayOpacity = settings.OverlayOpacity;
            _textColorMode = settings.OverlayTextColor ?? "Auto";
            SetupAutoCloseTimer(settings.OverlayTimeoutSeconds);
            SetupBackgroundAnalysisTimer();

            Debug.WriteLine($"[Overlay] Created with text color mode: {_textColorMode}");
        }

        #endregion Public Constructors

        #region Public Properties

        public int AutoHideSeconds { get; set; } = 2;
        public double OverlayOpacity { get; set; } = 0.9;

        #endregion Public Properties

        // "Auto", "White", "Black"
        // Cache for auto-detected color
        // Store text color setting directly in overlay

        #region Public Methods

        /// <summary>
        /// Displays volume levels on the overlay window and updates visual/text color logic if needed.
        /// </summary>
        /// <param name="volumes">A list of volume levels for each channel (0.0 to 1.0).</param>
        /// <param name="channelLabels">Optional list of labels for each channel. If null, defaults to "Ch 1", "Ch 2", etc.</param>
        public void ShowVolumes(List<float> volumes, List<string> channelLabels = null)
        {
            // REDUCE throttling for better responsiveness (50ms -> 16ms for ~60fps)
            if (DateTime.Now.Subtract(_lastVolumeUpdate).TotalMilliseconds < 16)
            {
                return;
            }
            _lastVolumeUpdate = DateTime.Now;

            _volumes = new List<float>(volumes);

            if (channelLabels != null && channelLabels.Count == volumes.Count)
            {
                _channelLabels = new List<string>(channelLabels);
            }
            else
            {
                _channelLabels = volumes.Select((_, i) => $"Ch {i + 1}").ToList();
            }

            UpdateWindowSizeToContent();

            // OPTIMIZE: Only start background analysis if really needed
            if (_textColorMode == "Auto" && !_backgroundAnalysisTimer.IsEnabled)
            {
                _backgroundAnalysisTimer.Start();
                // Don't block UI - do this async
                Task.Run(() =>
                {
                    try
                    {
                        AnalyzeBackgroundAndUpdateTextColor();
                    }
                    catch { }
                });
            }

            OverlayCanvas.InvalidateVisual();

            // FOCUS PREVENTION: Only change what's necessary
            if (!this.IsVisible)
            {
                // Use Visibility instead of Show() - this is the key fix
                this.Visibility = Visibility.Visible;

                // DON'T call Activate() - this was stealing focus
                // DON'T call SetWindowPos unless absolutely necessary
            }

            if (_autoCloseTimer != null)
            {
                _autoCloseTimer.Stop();
                _autoCloseTimer.Start();
            }
        }
        /// <summary>
                 /// Applies updated overlay settings such as opacity, position, auto-close timeout, and text color mode.
                 /// </summary>
                 /// <param name="settings">The updated settings to apply.</param>
        public void UpdateSettings(AppSettings settings)
        {
            // Prevents unnecessary updates or recursion during settings application
            _isUpdatingSettings = true;

            // Apply the overlay opacity from the settings (0.0 to 1.0)
            OverlayOpacity = settings.OverlayOpacity;

            // Determine text color mode, fallback to "Auto" if not set
            _textColorMode = settings.OverlayTextColor ?? "Auto";

            // If not currently being dragged and valid position is set, reposition the overlay
            if (!_isDragging && settings.OverlayX != 0 && settings.OverlayY != 0)
            {
                SetPrecisePosition(settings.OverlayX, settings.OverlayY);
            }

            // Setup the auto-close timer with the configured timeout value (or disable if set to 0/-1)
            SetupAutoCloseTimer(settings.OverlayTimeoutSeconds);

            // If text color mode is "Auto", start background analysis for optimal text visibility
            if (_textColorMode == "Auto")
            {
                _backgroundAnalysisTimer?.Start(); // Begin continuous background brightness checks
                AnalyzeBackgroundAndUpdateTextColor(); // Perform one-time immediate analysis
            }
            else
            {
                // Stop background analysis if static color mode is selected (e.g. White/Black)
                _backgroundAnalysisTimer?.Stop();
            }

            // Force the canvas to repaint using the new settings
            OverlayCanvas?.InvalidateVisual();

            // Re-enable updates
            _isUpdatingSettings = false;

            // Log the change for debugging
            Debug.WriteLine($"[Overlay] Settings updated - Text color mode: {_textColorMode}");
        }


        #endregion Public Methods

        #region Protected Methods

        protected override void OnClosed(EventArgs e)
        {
            _backgroundAnalysisTimer?.Stop();
            base.OnClosed(e);
        }
        public new void Show()
        {
            try
            {
                // Don't call base.Show() as it can steal focus
                this.Visibility = Visibility.Visible;

                // If you need to ensure it's topmost, do it without activation
                if (this.IsLoaded)
                {
                    var helper = new WindowInteropHelper(this);
                    if (helper.Handle != IntPtr.Zero)
                    {
                        SetWindowPos(helper.Handle, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] Error in custom Show: {ex.Message}");
            }
        }
        public new void Hide()
        {
            try
            {
                this.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] Error in Hide: {ex.Message}");
            }
        }

        // Prevent any focus-related events
        protected override void OnGotFocus(RoutedEventArgs e)
        {
            // Don't call base to prevent focus handling
            e.Handled = true;
        }

        protected override void OnActivated(EventArgs e)
        {
            // Don't call base to prevent activation
        }
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            try
            {
                var helper = new WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;

                // Minimal Win32 fix - just prevent activation
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                exStyle |= WS_EX_NOACTIVATE;
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

                Debug.WriteLine("[Overlay] Applied WS_EX_NOACTIVATE");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] Error setting window styles: {ex.Message}");
            }
        }

        #endregion Protected Methods

        #region Private Methods

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hDestDC, int x, int y, int nWidth, int nHeight, IntPtr hSrcDC, int xSrc, int ySrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        private static extern int GetPixel(IntPtr hDC, int x, int y);

        [DllImport("user32.dll")]
        private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hGDIObj);
        /// <summary>
        /// Analyzes the background behind the overlay and updates the text color
        /// based on average luminance to ensure optimal contrast (white or black text).
        /// </summary>
        private void AnalyzeBackgroundAndUpdateTextColor()
        {
            try
            {
                // Capture the average color of the background behind the overlay
                var averageColor = CaptureBackgroundColor();

                // Calculate perceived brightness (luminance) from the average color
                var luminance = CalculateLuminance(averageColor);

                // Decide on white text if the background is dark (luminance ≤ 128)
                bool shouldUseWhiteText = luminance <= 128;

                // Only update the overlay if the optimal color has changed
                if (_isWhiteTextOptimal != shouldUseWhiteText)
                {
                    _isWhiteTextOptimal = shouldUseWhiteText;

                    // Request the canvas to redraw with the new text color
                    OverlayCanvas?.InvalidateVisual();

                    Debug.WriteLine($"[Overlay] Auto-detected text color: {(shouldUseWhiteText ? "White" : "Black")} (luminance: {luminance:F1})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] Error analyzing background: {ex.Message}");

                // Fallback to white text if background analysis fails
                _isWhiteTextOptimal = true;
            }
        }

        /// <summary>
        /// Timer tick handler that periodically analyzes the background
        /// to dynamically adjust the text color if in "Auto" mode.
        /// </summary>
        private void BackgroundAnalysisTimer_Tick(object sender, EventArgs e)
        {
            if (_textColorMode == "Auto" && this.IsVisible)
            {
                AnalyzeBackgroundAndUpdateTextColor();
            }
        }

        /// <summary>
        /// Calculates the perceived luminance of a given color
        /// using standard NTSC weights for RGB.
        /// </summary>
        private double CalculateLuminance(System.Drawing.Color color)
        {
            return 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        }

        /// <summary>
        /// Captures the average color of the screen behind the overlay
        /// by sampling multiple points across the overlay window area.
        /// </summary>
        private System.Drawing.Color CaptureBackgroundColor()
        {
            int width = (int)this.ActualWidth;
            int height = (int)this.ActualHeight;
            int x = (int)this.Left;
            int y = (int)this.Top;

            // Define sampling points in a grid across the overlay
            var samplePoints = new List<System.Drawing.Point>
    {
        new System.Drawing.Point(x + width / 4, y + height / 4),
        new System.Drawing.Point(x + 3 * width / 4, y + height / 4),
        new System.Drawing.Point(x + width / 2, y + height / 2),
        new System.Drawing.Point(x + width / 4, y + 3 * height / 4),
        new System.Drawing.Point(x + 3 * width / 4, y + 3 * height / 4)
    };

            var colors = new List<System.Drawing.Color>();

            // Attempt to sample color from each point
            foreach (var point in samplePoints)
            {
                try
                {
                    var color = GetPixelColor(point.X, point.Y);
                    colors.Add(color);
                }
                catch
                {
                    // Ignore failed samples (e.g. off-screen)
                }
            }

            if (colors.Count == 0)
            {
                // Fallback to gray if no valid samples
                return System.Drawing.Color.Gray;
            }

            // Calculate average RGB values across all sampled points
            int avgR = (int)colors.Average(c => c.R);
            int avgG = (int)colors.Average(c => c.G);
            int avgB = (int)colors.Average(c => c.B);

            return System.Drawing.Color.FromArgb(avgR, avgG, avgB);
        }

        /// <summary>
        /// Draws a circular volume meter with foreground arc and center percentage label,
        /// along with a text label below.
        /// </summary>
        private void DrawCircularMeter(SKCanvas canvas, SKPoint center, float diameter, float value, string label, float labelOffset)
        {
            var radius = diameter / 2f;
            var thickness = 10f;

            // Background arc (dimmed white)
            using var bgPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(60),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                IsAntialias = true
            };

            // Foreground arc (volume level color)
            using var fgPaint = new SKPaint
            {
                Color = GetVolumeColor(value).WithAlpha(230),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };

            // Draw circular background ring
            canvas.DrawCircle(center.X, center.Y, radius - thickness / 2, bgPaint);

            // Draw foreground arc for non-zero volumes
            if (value > 0.01f)
            {
                float startAngle = -90f; // Start at top
                float sweepAngle = 360f * value;

                var rect = new SKRect(
                    center.X - radius + thickness / 2,
                    center.Y - radius + thickness / 2,
                    center.X + radius - thickness / 2,
                    center.Y + radius - thickness / 2);

                using var path = new SKPath();
                path.AddArc(rect, startAngle, sweepAngle);
                canvas.DrawPath(path, fgPaint);
            }

            // Determine text color (based on user setting or luminance analysis)
            SKColor textColor = GetTextColor();

            // Draw volume percentage text in center
            using var volumeTextPaint = new SKPaint
            {
                Color = textColor,
                TextSize = 16,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
            };

            string volumeText = $"{(value * 100):F0}%";
            canvas.DrawText(volumeText, center.X, center.Y + 6, volumeTextPaint);

            // Draw the channel label below the meter
            DrawWrappedLabel(canvas, label, center.X, center.Y + labelOffset);
        }


        /// <summary>
        /// Draws a wrapped label centered horizontally at a specified Y coordinate,
        /// splitting the text into multiple lines if needed, with basic word-wrapping and truncation.
        /// </summary>
        private void DrawWrappedLabel(SKCanvas canvas, string text, float centerX, float startY)
        {
            // Determine text color based on mode (Auto, White, Black)
            SKColor textColor = GetTextColor();

            // Configure paint settings for the label text
            using var labelPaint = new SKPaint
            {
                Color = textColor,
                TextSize = 11,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
            };

            const float maxWidth = 120f;     // Maximum allowed line width in pixels
            const float lineHeight = 13f;    // Vertical space per line

            // Split text into words for wrapping
            var words = text.Split(' ');
            var lines = new List<string>();
            var currentLine = "";

            // Perform simple word-wrapping by building up lines
            foreach (var word in words)
            {
                var testLine = string.IsNullOrEmpty(currentLine) ? word : $"{currentLine} {word}";
                var bounds = new SKRect();
                labelPaint.MeasureText(testLine, ref bounds);

                if (bounds.Width <= maxWidth || string.IsNullOrEmpty(currentLine))
                {
                    currentLine = testLine;
                }
                else
                {
                    lines.Add(currentLine);
                    currentLine = word;
                }
            }

            // Add final line if non-empty
            if (!string.IsNullOrEmpty(currentLine))
            {
                lines.Add(currentLine);
            }

            // Limit to max 2 lines and truncate second line if needed
            if (lines.Count > 2)
            {
                lines = lines.Take(2).ToList();
                if (lines.Count == 2 && lines[1].Length > 10)
                {
                    lines[1] = lines[1].Substring(0, 10) + "...";
                }
            }

            // Center the block of lines vertically around startY
            float totalHeight = lines.Count * lineHeight;
            float currentY = startY - (totalHeight / 2) + (lineHeight / 2);

            // Draw each line of text
            foreach (var line in lines)
            {
                canvas.DrawText(line, centerX, currentY, labelPaint);
                currentY += lineHeight;
            }
        }

        /// <summary>
        /// Retrieves the color of a specific screen pixel using Win32 API calls.
        /// </summary>
        private System.Drawing.Color GetPixelColor(int x, int y)
        {
            IntPtr desk = GetDC(IntPtr.Zero);              // Get device context for entire screen
            int color = GetPixel(desk, x, y);              // Get color at specified screen coordinates
            ReleaseDC(IntPtr.Zero, desk);                  // Release device context

            // Convert the pixel value (0xBBGGRR) to a Color object
            return System.Drawing.Color.FromArgb(
                (color >> 0) & 0xFF,  // Blue
                (color >> 8) & 0xFF,  // Green
                (color >> 16) & 0xFF  // Red
            );
        }

        /// <summary>
        /// Returns the SKColor to use for overlay text based on the current text color mode.
        /// </summary>
        private SKColor GetTextColor()
        {
            return _textColorMode switch
            {
                "White" => SKColors.White.WithAlpha(255),
                "Black" => SKColors.Black.WithAlpha(255),
                "Auto" => _isWhiteTextOptimal ? SKColors.White.WithAlpha(255) : SKColors.Black.WithAlpha(255),
                _ => SKColors.White.WithAlpha(255)
            };
        }

        /// <summary>
        /// Returns a color representing the current volume level, used for the meter fill.
        /// </summary>
        private SKColor GetVolumeColor(float volume)
        {
            if (volume < 0.01f)
                return SKColors.DarkGray;     // Muted or silence
            else if (volume < 0.66f)
                return SKColors.LimeGreen;    // Safe level
            else if (volume < 0.8f)
                return SKColors.Gold;         // Moderate level
            else if (volume < 0.9f)
                return SKColors.Orange;       // High level
            else
                return SKColors.Red;          // Clipping or very loud
        }


        /// <summary>
        /// Handles the paint event for the overlay canvas.
        /// Draws the semi-transparent background, border, and all volume meters with labels.
        /// </summary>
        private void OverlayCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;

            // Clear the canvas with transparent background
            canvas.Clear(SKColors.Transparent);

            // If there are no volume values to show, skip rendering
            if (_volumes.Count == 0) return;

            // Create a semi-transparent gray background fill
            using (var backgroundPaint = new SKPaint
            {
                Color = SKColors.Gray.WithAlpha((byte)(OverlayOpacity * 255 * 0.75)),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            })
            {
                // Define the background area with padding
                var backgroundRect = new SKRect(5, 5, e.Info.Width - 5, e.Info.Height - 5);

                // Draw a rounded rectangle as the background
                canvas.DrawRoundRect(backgroundRect, 12, 12, backgroundPaint);

                // Draw a white border around the background
                using (var borderPaint = new SKPaint
                {
                    Color = SKColors.White.WithAlpha((byte)(OverlayOpacity * 255 * 0.4)),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.5f,
                    IsAntialias = true
                })
                {
                    canvas.DrawRoundRect(backgroundRect, 12, 12, borderPaint);

                    // Determine how many channels to draw per row (clamped to max)
                    int channelsPerRow = Math.Min(_volumes.Count, MaxChannelsPerRow);

                    // Loop through all volume channels and draw each as a circular meter
                    for (int i = 0; i < _volumes.Count; i++)
                    {
                        // Determine row and column index
                        int row = i / channelsPerRow;
                        int col = i % channelsPerRow;

                        // Calculate center position for each circular meter
                        float x = Padding + (col * HorizontalSpacing) + (MeterSize / 2);
                        float y = Padding + (row * VerticalSpacing) + (MeterSize / 2);

                        // Get the volume value and label for this channel
                        float volume = _volumes[i];
                        string label = i < _channelLabels.Count ? _channelLabels[i] : $"Ch {i + 1}";

                        // Draw the circular volume meter with label
                        DrawCircularMeter(canvas, new SKPoint(x, y), MeterSize, volume, label, LabelOffset);
                    }
                }
            }
        }


        /// <summary>
        /// Sets the window position while clamping it within virtual screen bounds.
        /// Prevents the overlay from being placed too far offscreen.
        /// </summary>
        private void SetPrecisePosition(double x, double y)
        {
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            var virtualRight = virtualLeft + virtualWidth;
            var virtualBottom = virtualTop + virtualHeight;

            // Clamp X position within bounds
            if (x < virtualLeft - 200 || x > virtualRight - 50)
            {
                x = Math.Max(virtualLeft, Math.Min(x, virtualRight - 200));
            }

            // Clamp Y position within bounds
            if (y < virtualTop - 100 || y > virtualBottom - 50)
            {
                y = Math.Max(virtualTop, Math.Min(y, virtualBottom - 100));
            }

            // Set final position, rounded to 1 decimal place
            this.Left = Math.Round(x, 1);
            this.Top = Math.Round(y, 1);
        }

        /// <summary>
        /// Sets up a timer that hides the overlay after a specified number of seconds.
        /// </summary>
        private void SetupAutoCloseTimer(int timeoutSeconds)
        {
            _autoCloseTimer?.Stop();
            _autoCloseTimer = null;

            // Only set up timer if timeout is greater than 0
            if (timeoutSeconds > 0)
            {
                _autoCloseTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(timeoutSeconds)
                };
                _autoCloseTimer.Tick += (s, e) =>
                {
                    _autoCloseTimer.Stop();
                    this.Hide(); // Hide overlay when time expires
                };
            }
        }

        /// <summary>
        /// Configures the background analysis timer to run periodically
        /// and detect optimal text color based on background.
        /// </summary>
        private void SetupBackgroundAnalysisTimer()
        {
            _backgroundAnalysisTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2) // Controls how often background color is sampled
            };
            _backgroundAnalysisTimer.Tick += BackgroundAnalysisTimer_Tick;
        }

        /// <summary>
        /// Dynamically adjusts window size based on the number of visible volume meters.
        /// </summary>
        private void UpdateWindowSizeToContent()
        {
            if (_volumes.Count == 0)
            {
                this.Width = 200;
                this.Height = 120;
                return;
            }

            int channelsPerRow = Math.Min(_volumes.Count, MaxChannelsPerRow);
            int rows = (int)Math.Ceiling((double)_volumes.Count / channelsPerRow);

            float contentWidth = (channelsPerRow * HorizontalSpacing) - (HorizontalSpacing - MeterSize);
            float totalWidth = contentWidth + (Padding * 2);

            float meterRadius = MeterSize / 2f;
            float rowHeight = VerticalSpacing;
            float totalContentHeight = (rows * rowHeight) - (rowHeight - MeterSize);

            float lastRowCenterY = Padding + meterRadius + ((rows - 1) * VerticalSpacing);
            float labelBottomY = lastRowCenterY + LabelOffset + 15f;
            float totalHeight = labelBottomY + Padding;

            // Apply calculated size to window
            this.Width = Math.Max(Math.Ceiling(totalWidth), 200);
            this.Height = Math.Max(Math.Ceiling(totalHeight), 100);
        }

        /// <summary>
        /// Updates overlay position in main window settings when the window is moved.
        /// </summary>
        private void Window_LocationChanged(object sender, EventArgs e)
        {
            if (!_isUpdatingSettings && this.IsLoaded && Application.Current.MainWindow is MainWindow mainWindow)
            {
                var preciseX = Math.Round(this.Left, 1);
                var preciseY = Math.Round(this.Top, 1);

                // Notify main window of new position
                mainWindow.UpdateOverlayPosition(preciseX, preciseY);
            }
        }

        /// <summary>
        /// Allows the user to drag the overlay window with the mouse.
        /// </summary>
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try
                {
                    _isDragging = true;
                    this.DragMove(); // Initiates drag movement
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Overlay] Drag operation failed: {ex.Message}");
                }
                finally
                {
                    _isDragging = false;
                }
            }
        }


        #endregion Private Methods

      
    }
}