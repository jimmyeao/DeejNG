using DeejNG.Classes;
using System;
using System.Collections.Generic;

namespace DeejNG.Core.Interfaces
{
    public interface IOverlayService
    {
        #region Public Events

        event EventHandler OverlayHidden;

        event EventHandler<OverlayPositionChangedEventArgs> PositionChanged;

        #endregion Public Events

        #region Public Properties

        bool IsEnabled { get; set; }
        bool IsVisible { get; }
        double Opacity { get; set; }
        string TextColorMode { get; set; }
        int TimeoutSeconds { get; set; }
        double X { get; set; }
        double Y { get; set; }

        #endregion Public Properties

        #region Public Methods

        void Dispose();

        void ForceHide();

        void HideOverlay();

        void Initialize();

        void ShowOverlay(List<float> volumes, List<string> labels);
        void UpdatePosition(double x, double y);
        void UpdateSettings(AppSettings settings);

        #endregion Public Methods
    }

    public class OverlayPositionChangedEventArgs : EventArgs
    {
        #region Public Properties

        public double X { get; set; }
        public double Y { get; set; }

        #endregion Public Properties
    }
}
