// Updated FloatingOverlay.xaml.cs with automatic content-based resizing

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
        private bool _isUpdatingSettings = false;
        private bool _isDragging = false;

        // Layout constants - keep these consistent between sizing and painting
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

            this.AllowsTransparency = true;
            this.WindowStyle = WindowStyle.None;
            this.Background = null;
            this.Topmost = true;

            SetupAutoCloseTimer(settings.OverlayTimeoutSeconds);

            Debug.WriteLine($"[Overlay] Created at precise position ({this.Left}, {this.Top}) with opacity {OverlayOpacity} and timeout {settings.OverlayTimeoutSeconds}s");
        }

        private void SetPrecisePosition(double x, double y)
        {
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            var virtualRight = virtualLeft + virtualWidth;
            var virtualBottom = virtualTop + virtualHeight;

            Debug.WriteLine($"[Overlay] Virtual screen bounds: ({virtualLeft}, {virtualTop}) to ({virtualRight}, {virtualBottom})");
            Debug.WriteLine($"[Overlay] Requested position: ({x}, {y})");

            if (x < virtualLeft - 200 || x > virtualRight - 50)
            {
                Debug.WriteLine($"[Overlay] X position {x} is outside virtual bounds, clamping");
                x = Math.Max(virtualLeft, Math.Min(x, virtualRight - 200));
            }

            if (y < virtualTop - 100 || y > virtualBottom - 50)
            {
                Debug.WriteLine($"[Overlay] Y position {y} is outside virtual bounds, clamping");
                y = Math.Max(virtualTop, Math.Min(y, virtualBottom - 100));
            }

            this.Left = Math.Round(x, 1);
            this.Top = Math.Round(y, 1);

            Debug.WriteLine($"[Overlay] Final precise position set: ({this.Left}, {this.Top})");
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

            Debug.WriteLine($"[Overlay] UpdateSettings called with position: ({settings.OverlayX}, {settings.OverlayY})");

            OverlayOpacity = settings.OverlayOpacity;

            if (!_isDragging && settings.OverlayX != 0 && settings.OverlayY != 0)
            {
                Debug.WriteLine($"[Overlay] Updating to new position: ({settings.OverlayX}, {settings.OverlayY})");
                SetPrecisePosition(settings.OverlayX, settings.OverlayY);
            }
            else if (!_isDragging)
            {
                Debug.WriteLine("[Overlay] Settings position was 0,0 - keeping current position");
            }

            SetupAutoCloseTimer(settings.OverlayTimeoutSeconds);
            OverlayCanvas?.InvalidateVisual();

            _isUpdatingSettings = false;

            Debug.WriteLine($"[Overlay] Settings update complete - Opacity: {OverlayOpacity}, Final Position: ({this.Left}, {this.Top}), Timeout: {settings.OverlayTimeoutSeconds}s");
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

            // Update window size based on actual content
            UpdateWindowSizeToContent();

            OverlayCanvas.InvalidateVisual();

            this.Show();
            this.Activate();

            if (_autoCloseTimer != null)
            {
                Debug.WriteLine("[Overlay] Restarting auto-close timer");
                _autoCloseTimer.Stop();
                _autoCloseTimer.Start();
            }
        }

        private void UpdateWindowSizeToContent()
        {
            if (_volumes.Count == 0)
            {
                this.Width = 200;
                this.Height = 120;
                return;
            }

            // Calculate exact content bounds
            int channelsPerRow = Math.Min(_volumes.Count, MaxChannelsPerRow);
            int rows = (int)Math.Ceiling((double)_volumes.Count / channelsPerRow);

            // Calculate actual content width
            float contentWidth = (channelsPerRow * HorizontalSpacing) - (HorizontalSpacing - MeterSize);
            float totalWidth = contentWidth + (Padding * 2);

            // Calculate actual content height more precisely
            float meterRadius = MeterSize / 2f;

            // For each row: meter center + radius + label extension
            float rowHeight = VerticalSpacing;
            float totalContentHeight = (rows * rowHeight) - (rowHeight - MeterSize);

            // Add space for labels extending below the last row
            float lastRowCenterY = Padding + meterRadius + ((rows - 1) * VerticalSpacing);
            float labelBottomY = lastRowCenterY + LabelOffset + 15f; // 15f for text height
            float totalHeight = labelBottomY + Padding;

            // Apply calculated size
            this.Width = Math.Max(Math.Ceiling(totalWidth), 200);
            this.Height = Math.Max(Math.Ceiling(totalHeight), 100);

            Debug.WriteLine($"[Overlay] Resized to content: {this.Width}x{this.Height} for {_volumes.Count} channels in {rows} rows");
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

                Debug.WriteLine($"[Overlay] Position changed and saved: ({preciseX}, {preciseY})");
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

            // Use consistent constants
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

            // Volume percentage in center
            using var volumeTextPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(255),
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
            using var labelPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(220),
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