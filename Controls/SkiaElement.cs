using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace DeejNG.Controls
{
    /// <summary>
    /// Minimal replacement for SkiaSharp.Views.WPF's SKElement, hosting an SKSurface
    /// on top of a WriteableBitmap so we can depend on SkiaSharp core only (no
    /// net462 wrapper / transitive OpenTK).
    /// </summary>
    public class SkiaElement : FrameworkElement
    {
        private WriteableBitmap? _bitmap;

        public event EventHandler<SkiaPaintSurfaceEventArgs>? PaintSurface;

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var dpi = VisualTreeHelper.GetDpi(this);
            int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);

            if (width <= 0 || height <= 0)
                return;

            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
            }

            _bitmap.Lock();
            try
            {
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var surface = SKSurface.Create(info, _bitmap.BackBuffer, _bitmap.BackBufferStride))
                {
                    surface.Canvas.Clear(SKColors.Transparent);
                    PaintSurface?.Invoke(this, new SkiaPaintSurfaceEventArgs(surface, info));
                    surface.Canvas.Flush();
                }

                _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                _bitmap.Unlock();
            }

            drawingContext.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            InvalidateVisual();
        }
    }

    public sealed class SkiaPaintSurfaceEventArgs : EventArgs
    {
        public SkiaPaintSurfaceEventArgs(SKSurface surface, SKImageInfo info)
        {
            Surface = surface;
            Info = info;
        }

        public SKSurface Surface { get; }

        public SKImageInfo Info { get; }
    }
}
