// Fixed FloatingOverlay.xaml.cs - all issues addressed
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
        private bool _isUpdatingSettings = false; // Prevent position reset during updates

        public FloatingOverlay(AppSettings settings, MainWindow parentWindow = null)
        {
            InitializeComponent();

            _parentWindow = parentWindow;

            this.Opacity = 1.0; // Window always fully opaque

            // Set initial position from settings
            this.Left = settings.OverlayX > 0 ? settings.OverlayX : 100;
            this.Top = settings.OverlayY > 0 ? settings.OverlayY : 100;

            OverlayOpacity = settings.OverlayOpacity;

            this.AllowsTransparency = true;
            this.WindowStyle = WindowStyle.None;
            this.Background = null;
            this.Topmost = true;

            SetupAutoCloseTimer(settings.OverlayTimeoutSeconds);

            Debug.WriteLine($"[Overlay] Created at position ({this.Left}, {this.Top}) with opacity {OverlayOpacity} and timeout {settings.OverlayTimeoutSeconds}s");
        }

        private void SetupAutoCloseTimer(int timeoutSeconds)
        {
            _autoCloseTimer?.Stop();
            _autoCloseTimer = null;

            Debug.WriteLine($"[Overlay] Setting up auto-close timer: {timeoutSeconds} seconds");

            if (timeoutSeconds > 0)
            {
                _autoCloseTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(timeoutSeconds)
                };
                _autoCloseTimer.Tick += (s, e) =>
                {
                    Debug.WriteLine("[Overlay] Auto-close timer triggered - hiding overlay");
                    _autoCloseTimer.Stop();
                    this.Hide();
                };
                Debug.WriteLine($"[Overlay] Auto-close timer created with {timeoutSeconds}s interval");
            }
            else
            {
                Debug.WriteLine("[Overlay] Auto-close disabled (timeout = 0)");
            }
        }

        public void UpdateSettings(AppSettings settings)
        {
            _isUpdatingSettings = true;

            // Update opacity but preserve position
            OverlayOpacity = settings.OverlayOpacity;

            // Only update position if it's not zero (avoid jumping to 0,0)
            if (settings.OverlayX > 0 && settings.OverlayY > 0)
            {
                this.Left = settings.OverlayX;
                this.Top = settings.OverlayY;
            }

            // Update auto-close timer immediately
            SetupAutoCloseTimer(settings.OverlayTimeoutSeconds);

            // Force redraw with new opacity
            OverlayCanvas?.InvalidateVisual();

            _isUpdatingSettings = false;

            Debug.WriteLine($"[Overlay] Settings updated - Opacity: {OverlayOpacity}, Position: ({this.Left}, {this.Top}), Timeout: {settings.OverlayTimeoutSeconds}s");
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

            UpdateWindowSize();
            OverlayCanvas.InvalidateVisual();

            this.Show();
            this.Activate();

            // Restart auto-close timer if enabled
            if (_autoCloseTimer != null)
            {
                Debug.WriteLine("[Overlay] Restarting auto-close timer");
                _autoCloseTimer.Stop();
                _autoCloseTimer.Start();
            }
        }

        private void UpdateWindowSize()
        {
            if (_volumes.Count == 0) return;

            const float meterSize = 90f;
            const float horizontalSpacing = 130f;
            const float padding = 25f;
            const float labelSpace = 35f; // Reduced label space

            int channelsPerRow = Math.Min(_volumes.Count, 6);
            int rows = (int)Math.Ceiling((double)_volumes.Count / channelsPerRow);

            float totalWidth = (channelsPerRow * horizontalSpacing) + (padding * 2) - (horizontalSpacing - meterSize);
            float totalHeight = (rows * (meterSize + labelSpace + 10f)) + (padding * 2); // Reduced height

            this.Width = Math.Max(totalWidth, 200);
            this.Height = Math.Max(totalHeight, 100); // Reduced minimum height
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
                    this.DragMove();
                }
                catch
                {
                    /* ignore drag exceptions */
                }
            }
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            // Don't save position if we're updating settings (to prevent jumping)
            if (!_isUpdatingSettings && Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.UpdateOverlayPosition(this.Left, this.Top);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            this.LocationChanged += Window_LocationChanged;
            UpdateWindowSize();
        }

        private void OverlayCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            if (_volumes.Count == 0) return;

            const float meterSize = 90f;
            const float horizontalSpacing = 130f;
            const float verticalSpacing = 125f; // Reduced spacing
            const float padding = 25f;
            const float labelOffset = 65f; // Moved labels further down

            // Draw background with ONLY background opacity
            var backgroundPaint = new SKPaint
            {
                Color = SKColors.Gray.WithAlpha((byte)(OverlayOpacity * 255 * 0.75)),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            var backgroundRect = new SKRect(5, 5, e.Info.Width - 5, e.Info.Height - 5);
            canvas.DrawRoundRect(backgroundRect, 12, 12, backgroundPaint);

            // Border with background opacity
            var borderPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)(OverlayOpacity * 255 * 0.4)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                IsAntialias = true
            };
            canvas.DrawRoundRect(backgroundRect, 12, 12, borderPaint);

            int channelsPerRow = Math.Min(_volumes.Count, 6);

            for (int i = 0; i < _volumes.Count; i++)
            {
                int row = i / channelsPerRow;
                int col = i % channelsPerRow;

                float x = padding + (col * horizontalSpacing) + (meterSize / 2);
                float y = padding + (row * verticalSpacing) + (meterSize / 2);

                float volume = _volumes[i];
                string label = i < _channelLabels.Count ? _channelLabels[i] : $"Ch {i + 1}";

                DrawCircularMeter(canvas, new SKPoint(x, y), meterSize, volume, label, labelOffset);
            }

            backgroundPaint.Dispose();
            borderPaint.Dispose();
        }

        private void DrawCircularMeter(SKCanvas canvas, SKPoint center, float diameter, float value, string label, float labelOffset)
        {
            var radius = diameter / 2f;
            var thickness = 10f;

            // Background circle - FIXED opacity (not affected by background transparency)
            using var bgPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(60), // Fixed low opacity
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                IsAntialias = true
            };

            // Foreground arc - FIXED opacity (always visible)
            using var fgPaint = new SKPaint
            {
                Color = GetVolumeColor(value).WithAlpha(230), // Fixed high opacity
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

            // Volume percentage in center - FIXED opacity
            using var volumeTextPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(255), // Always fully visible
                TextSize = 16,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
            };

            string volumeText = $"{(value * 100):F0}%";
            canvas.DrawText(volumeText, center.X, center.Y + 6, volumeTextPaint);

            // Draw wrapped label below meter - FIXED opacity
            DrawWrappedLabel(canvas, label, center.X, center.Y + labelOffset);
        }

        private void DrawWrappedLabel(SKCanvas canvas, string text, float centerX, float startY)
        {
            using var labelPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(220), // Fixed opacity - always readable
                TextSize = 11, // Slightly smaller text
                IsAntialias = true,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
            };

            const float maxWidth = 120f; // Slightly wider
            const float lineHeight = 13f; // Tighter line spacing

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

            // Limit to 2 lines
            if (lines.Count > 2)
            {
                lines = lines.Take(2).ToList();
                if (lines.Count == 2 && lines[1].Length > 10)
                {
                    lines[1] = lines[1].Substring(0, 10) + "...";
                }
            }

            // Draw each line
            float totalHeight = lines.Count * lineHeight;
            float currentY = startY - (totalHeight / 2) + (lineHeight / 2);

            foreach (var line in lines)
            {
                canvas.DrawText(line, centerX, currentY, labelPaint);
                currentY += lineHeight;
            }
        }

        private SKColor GetVolumeColor(float volume)
        {
            if (volume < 0.01f)
                return SKColors.DarkGray;
            else if (volume < 0.5f)
                return SKColors.LimeGreen;
            else if (volume < 0.75f)
                return SKColors.Gold;
            else if (volume < 0.9f)
                return SKColors.Orange;
            else
                return SKColors.Red;
        }
    }
}