// Updated FloatingOverlay.xaml.cs with proper text color handling

using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DeejNG.Classes;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeejNG.Views
{
    public partial class FloatingOverlay : Window
    {
        private readonly DispatcherTimer _hideTimer = new();
        private List<float> _volumes = new();
        private List<string> _channelLabels = new();
        private DispatcherTimer? _autoCloseTimer;
        private MainWindow _parentWindow;
        public double OverlayOpacity { get; set; } = 0.9;
        public int AutoHideSeconds { get; set; } = 2;
        private bool _isUpdatingSettings = false;
        private bool _isDragging = false;
        private string _textColorMode = "Auto"; // "Auto", "White", "Black"
        private DispatcherTimer _backgroundAnalysisTimer;
        private bool _isWhiteTextOptimal = true; // Cache for auto-detected color
        // Store text color setting directly in overlay
        private bool _useWhiteText = true;
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hGDIObj);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hDestDC, int x, int y, int nWidth, int nHeight, IntPtr hSrcDC, int xSrc, int ySrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);
        // Layout constants
        private const float MeterSize = 90f;
        private const float HorizontalSpacing = 130f;
        private const float VerticalSpacing = 125f;
        private const float Padding = 25f;
        private const float LabelOffset = 65f;
        private const int MaxChannelsPerRow = 6;

        public FloatingOverlay(AppSettings settings, MainWindow parentWindow = null)
        {
            InitializeComponent();

            _parentWindow = parentWindow;
            this.Opacity = 1.0;

            SetPrecisePosition(settings.OverlayX, settings.OverlayY);
            OverlayOpacity = settings.OverlayOpacity;
            _textColorMode = settings.OverlayTextColor ?? "Auto";

            this.AllowsTransparency = true;
            this.WindowStyle = WindowStyle.None;
            this.Background = null;
            this.Topmost = true;

            SetupAutoCloseTimer(settings.OverlayTimeoutSeconds);
            SetupBackgroundAnalysisTimer();

            Debug.WriteLine($"[Overlay] Created with text color mode: {_textColorMode}");
        }

        private void SetupBackgroundAnalysisTimer()
        {
            // Timer to periodically check background color when in Auto mode
            _backgroundAnalysisTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2) // Check every 2 seconds
            };
            _backgroundAnalysisTimer.Tick += BackgroundAnalysisTimer_Tick;
        }

        private void BackgroundAnalysisTimer_Tick(object sender, EventArgs e)
        {
            if (_textColorMode == "Auto" && this.IsVisible)
            {
                AnalyzeBackgroundAndUpdateTextColor();
            }
        }

        private void SetPrecisePosition(double x, double y)
        {
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            var virtualRight = virtualLeft + virtualWidth;
            var virtualBottom = virtualTop + virtualHeight;

            if (x < virtualLeft - 200 || x > virtualRight - 50)
            {
                x = Math.Max(virtualLeft, Math.Min(x, virtualRight - 200));
            }

            if (y < virtualTop - 100 || y > virtualBottom - 50)
            {
                y = Math.Max(virtualTop, Math.Min(y, virtualBottom - 100));
            }

            this.Left = Math.Round(x, 1);
            this.Top = Math.Round(y, 1);
        }

        private void SetupAutoCloseTimer(int timeoutSeconds)
        {
            _autoCloseTimer?.Stop();
            _autoCloseTimer = null;

            if (timeoutSeconds > 0)
            {
                _autoCloseTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(timeoutSeconds)
                };
                _autoCloseTimer.Tick += (s, e) =>
                {
                    _autoCloseTimer.Stop();
                    this.Hide();
                };
            }
        }

        public void UpdateSettings(AppSettings settings)
        {
            _isUpdatingSettings = true;

            OverlayOpacity = settings.OverlayOpacity;
            _textColorMode = settings.OverlayTextColor ?? "Auto";

            if (!_isDragging && settings.OverlayX != 0 && settings.OverlayY != 0)
            {
                SetPrecisePosition(settings.OverlayX, settings.OverlayY);
            }

            SetupAutoCloseTimer(settings.OverlayTimeoutSeconds);

            // Start/stop background analysis based on mode
            if (_textColorMode == "Auto")
            {
                _backgroundAnalysisTimer?.Start();
                AnalyzeBackgroundAndUpdateTextColor(); // Immediate analysis
            }
            else
            {
                _backgroundAnalysisTimer?.Stop();
            }
            OverlayCanvas?.InvalidateVisual();

            _isUpdatingSettings = false;

            Debug.WriteLine($"[Overlay] Settings updated - Text color mode: {_textColorMode}");
        }

        public void ResetAutoHideTimer()
        {
            if (!IsVisible)
                Show();

            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(AutoHideSeconds);
            _hideTimer.Start();
        }

        public void ShowVolumes(List<float> volumes, List<string> channelLabels = null)
        {
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

            // Start background analysis if in Auto mode
            if (_textColorMode == "Auto")
            {
                _backgroundAnalysisTimer?.Start();
                AnalyzeBackgroundAndUpdateTextColor();
            }

            OverlayCanvas.InvalidateVisual();
            this.Show();
            this.Activate();

            if (_autoCloseTimer != null)
            {
                _autoCloseTimer.Stop();
                _autoCloseTimer.Start();
            }
        }

        private void AnalyzeBackgroundAndUpdateTextColor()
        {
            try
            {
                var averageColor = CaptureBackgroundColor();
                var luminance = CalculateLuminance(averageColor);

                // If luminance > 128, background is light, use black text
                // If luminance <= 128, background is dark, use white text
                bool shouldUseWhiteText = luminance <= 128;

                if (_isWhiteTextOptimal != shouldUseWhiteText)
                {
                    _isWhiteTextOptimal = shouldUseWhiteText;
                    OverlayCanvas?.InvalidateVisual(); // Redraw with new text color

                    Debug.WriteLine($"[Overlay] Auto-detected text color: {(shouldUseWhiteText ? "White" : "Black")} (luminance: {luminance:F1})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] Error analyzing background: {ex.Message}");
                _isWhiteTextOptimal = true; // Fallback to white
            }
        }
        private System.Drawing.Color CaptureBackgroundColor()
        {
            int width = (int)this.ActualWidth;
            int height = (int)this.ActualHeight;
            int x = (int)this.Left;
            int y = (int)this.Top;

            // Sample multiple points across the overlay area
            var samplePoints = new List<System.Drawing.Point>
            {
                new System.Drawing.Point(x + width / 4, y + height / 4),
                new System.Drawing.Point(x + 3 * width / 4, y + height / 4),
                new System.Drawing.Point(x + width / 2, y + height / 2),
                new System.Drawing.Point(x + width / 4, y + 3 * height / 4),
                new System.Drawing.Point(x + 3 * width / 4, y + 3 * height / 4)
            };

            var colors = new List<System.Drawing.Color>();

            foreach (var point in samplePoints)
            {
                try
                {
                    var color = GetPixelColor(point.X, point.Y);
                    colors.Add(color);
                }
                catch
                {
                    // Skip failed samples
                }
            }

            if (colors.Count == 0)
            {
                return System.Drawing.Color.Gray; // Fallback
            }

            // Calculate average color
            int avgR = (int)colors.Average(c => c.R);
            int avgG = (int)colors.Average(c => c.G);
            int avgB = (int)colors.Average(c => c.B);

            return System.Drawing.Color.FromArgb(avgR, avgG, avgB);
        }

        private System.Drawing.Color GetPixelColor(int x, int y)
        {
            IntPtr desk = GetDC(IntPtr.Zero);
            int color = GetPixel(desk, x, y);
            ReleaseDC(IntPtr.Zero, desk);

            return System.Drawing.Color.FromArgb(
                (color >> 0) & 0xFF,  // Blue
                (color >> 8) & 0xFF,  // Green
                (color >> 16) & 0xFF  // Red
            );
        }

        [DllImport("gdi32.dll")]
        private static extern int GetPixel(IntPtr hDC, int x, int y);

        private double CalculateLuminance(System.Drawing.Color color)
        {
            // Standard luminance calculation
            return 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        }

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
        protected override void OnClosed(EventArgs e)
        {
            _backgroundAnalysisTimer?.Stop();
            base.OnClosed(e);
        }

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

            this.Width = Math.Max(Math.Ceiling(totalWidth), 200);
            this.Height = Math.Max(Math.Ceiling(totalHeight), 100);
        }

        public void ResetAutoCloseTimer(int timeoutSeconds)
        {
            SetupAutoCloseTimer(timeoutSeconds);
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try
                {
                    _isDragging = true;
                    this.DragMove();
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

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            if (!_isUpdatingSettings && this.IsLoaded && Application.Current.MainWindow is MainWindow mainWindow)
            {
                var preciseX = Math.Round(this.Left, 1);
                var preciseY = Math.Round(this.Top, 1);

                mainWindow.UpdateOverlayPosition(preciseX, preciseY);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            this.LocationChanged += Window_LocationChanged;
            UpdateWindowSizeToContent();
        }

        private void OverlayCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            if (_volumes.Count == 0) return;

            using (var backgroundPaint = new SKPaint
            {
                Color = SKColors.Gray.WithAlpha((byte)(OverlayOpacity * 255 * 0.75)),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            })
            {
                var backgroundRect = new SKRect(5, 5, e.Info.Width - 5, e.Info.Height - 5);
                canvas.DrawRoundRect(backgroundRect, 12, 12, backgroundPaint);

                using (var borderPaint = new SKPaint
                {
                    Color = SKColors.White.WithAlpha((byte)(OverlayOpacity * 255 * 0.4)),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.5f,
                    IsAntialias = true
                })
                {
                    canvas.DrawRoundRect(backgroundRect, 12, 12, borderPaint);

                    int channelsPerRow = Math.Min(_volumes.Count, MaxChannelsPerRow);

                    for (int i = 0; i < _volumes.Count; i++)
                    {
                        int row = i / channelsPerRow;
                        int col = i % channelsPerRow;

                        float x = Padding + (col * HorizontalSpacing) + (MeterSize / 2);
                        float y = Padding + (row * VerticalSpacing) + (MeterSize / 2);

                        float volume = _volumes[i];
                        string label = i < _channelLabels.Count ? _channelLabels[i] : $"Ch {i + 1}";

                        DrawCircularMeter(canvas, new SKPoint(x, y), MeterSize, volume, label, LabelOffset);
                    }
                }
            }
        }

        private void DrawCircularMeter(SKCanvas canvas, SKPoint center, float diameter, float value, string label, float labelOffset)
        {
            var radius = diameter / 2f;
            var thickness = 10f;

            using var bgPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(60),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                IsAntialias = true
            };

            using var fgPaint = new SKPaint
            {
                Color = GetVolumeColor(value).WithAlpha(230),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };

            canvas.DrawCircle(center.X, center.Y, radius - thickness / 2, bgPaint);

            if (value > 0.01f)
            {
                float startAngle = -90f;
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

            // Get text color based on setting
            SKColor textColor = GetTextColor();

            // Volume percentage in center
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

            DrawWrappedLabel(canvas, label, center.X, center.Y + labelOffset);
        }

        private void DrawWrappedLabel(SKCanvas canvas, string text, float centerX, float startY)
        {
            SKColor textColor = GetTextColor();

            using var labelPaint = new SKPaint
            {
                Color = textColor,
                TextSize = 11,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
            };

            const float maxWidth = 120f;
            const float lineHeight = 13f;

            var words = text.Split(' ');
            var lines = new List<string>();
            var currentLine = "";

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

            if (!string.IsNullOrEmpty(currentLine))
            {
                lines.Add(currentLine);
            }

            if (lines.Count > 2)
            {
                lines = lines.Take(2).ToList();
                if (lines.Count == 2 && lines[1].Length > 10)
                {
                    lines[1] = lines[1].Substring(0, 10) + "...";
                }
            }

            float totalHeight = lines.Count * lineHeight;
            float currentY = startY - (totalHeight / 2) + (lineHeight / 2);

            foreach (var line in lines)
            {
                canvas.DrawText(line, centerX, currentY, labelPaint);
                currentY += lineHeight;
            }
        }

        // Fixed method - no longer tries to access private _appSettings
      
        private SKColor GetVolumeColor(float volume)
        {
            if (volume < 0.01f)
                return SKColors.DarkGray;
            else if (volume < 0.66f)
                return SKColors.LimeGreen;
            else if (volume < 0.8f)
                return SKColors.Gold;
            else if (volume < 0.9f)
                return SKColors.Orange;
            else
                return SKColors.Red;
        }
    }
}