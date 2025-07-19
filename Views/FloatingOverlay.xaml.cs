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

namespace DeejNG.Views
{
    public partial class FloatingOverlay : Window
    {
        private readonly DispatcherTimer _hideTimer = new();
        private List<float> _volumes = new();
        private DispatcherTimer? _autoCloseTimer;
        public double OverlayOpacity { get; set; } = 0.9;
        public int AutoHideSeconds { get; set; } = 2;

        public FloatingOverlay(AppSettings settings)
        {
            InitializeComponent();

            this.Opacity = settings.OverlayOpacity;

      

            if (settings.OverlayTimeoutSeconds > 0)
            {
                _autoCloseTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(settings.OverlayTimeoutSeconds)
                };
                _autoCloseTimer.Tick += (s, e) =>
                {
                    _autoCloseTimer.Stop();
                    this.Hide();
                };
            }
        }



        public void ResetAutoHideTimer()
        {
            if (!IsVisible)
                Show();

            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(AutoHideSeconds);
            _hideTimer.Start();
        }
        public void ShowVolumes(List<float> volumes)
        {
            // TODO: update UI visuals here...

            this.Show();
            this.Activate(); // optional, if needed to bring forward

            if (_autoCloseTimer != null)
            {
                _autoCloseTimer.Stop();
                _autoCloseTimer.Start();
            }
        }


        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); } catch { /* ignore drag exceptions */ }
            }
        }

        private void OverlayCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var count = _volumes.Count;
            float size = 80f;
            float spacing = 100f;

            for (int i = 0; i < count; i++)
            {
                float x = 20 + i * spacing;
                float y = e.Info.Height / 2f;
                float volume = _volumes[i];

                DrawCircularMeter(canvas, new SKPoint(x, y), size, volume, $"Vol {i + 1}");
            }
        }

        private void DrawCircularMeter(SKCanvas canvas, SKPoint center, float radius, float value, string label)
        {
            var thickness = 8f;

            using var bgPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(50),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                IsAntialias = true
            };

            using var fgPaint = new SKPaint
            {
                Color = SKColors.DeepSkyBlue,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };

            float startAngle = -90f;
            float sweepAngle = 360f * value;

            var rect = new SKRect(
                center.X - radius / 2,
                center.Y - radius / 2,
                center.X + radius / 2,
                center.Y + radius / 2);

            canvas.DrawCircle(center.X, center.Y, radius / 2, bgPaint);
            using var path = new SKPath();
            path.AddArc(rect, startAngle, sweepAngle);
            canvas.DrawPath(path, fgPaint);

            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 12,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center
            };
            canvas.DrawText(label, center.X, center.Y + radius / 1.1f, textPaint);
        }
    }
}
