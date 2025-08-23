using DeejNG.Classes;
using System;
using System.Collections.Generic;

namespace DeejNG.Core.Interfaces
{
    public interface IOverlayService
    {
        bool IsEnabled { get; set; }
        bool IsVisible { get; }
        double Opacity { get; set; }
        int TimeoutSeconds { get; set; }
        string TextColorMode { get; set; }
        double X { get; set; }
        double Y { get; set; }

        void ShowOverlay(List<float> volumes, List<string> labels);
        void HideOverlay();
        void ForceHide();
        void UpdatePosition(double x, double y);
        void UpdateSettings(AppSettings settings);
        void Initialize();
        void Dispose();

        event EventHandler<OverlayPositionChangedEventArgs> PositionChanged;
        event EventHandler OverlayHidden;
    }

    public class OverlayPositionChangedEventArgs : EventArgs
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}
