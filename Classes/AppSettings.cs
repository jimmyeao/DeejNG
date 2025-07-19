using DeejNG.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeejNG.Classes
{
    public class AppSettings
    {
        #region Public Properties

        public bool DisableSmoothing { get; set; }
        public List<bool> InputModes { get; set; } = new();
        public bool IsDarkTheme { get; set; }
        public bool IsSliderInverted { get; set; }
        public List<bool> MuteStates { get; set; } = new();
        public string? PortName { get; set; }
        public List<List<AudioTarget>> SliderTargets { get; set; } = new();
        public bool StartMinimized { get; set; } = false;
        public bool StartOnBoot { get; set; }
        public bool VuMeters { get; set; } = true;
        public bool OverlayEnabled { get; set; } = false;
      
        public int OverlayTimeoutSeconds { get; set; } = 5;  // default 5 seconds
        public double OverlayX { get; set; }
        public double OverlayY { get; set; }
        public double OverlayOpacity { get; set; } = 0.85;


        #endregion Public Properties
    }
}
