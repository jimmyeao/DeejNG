using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DeejNG.Core.Interfaces;
using Microsoft.Win32;

namespace DeejNG.Core.Services
{
    /// <summary>
    /// Service to handle system power events (sleep, wake, shutdown)
    /// and coordinate application state recovery
    /// </summary>
    public class PowerManagementService : IPowerManagementService
    {
        #region Private Fields

        private const int RESUME_STABILIZATION_DELAY_MS = 2000;
        private bool _isResuming = false;
        private DateTime _lastResumeTime = DateTime.MinValue;

        #endregion Private Fields

        #region Public Constructors

        public PowerManagementService()
        {
            // Register for Windows power events
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;


        }

        #endregion Public Constructors

        #region Public Events

        public event EventHandler SessionEnding;

        public event EventHandler SystemResumed;

        public event EventHandler SystemResuming;

        public event EventHandler SystemSuspending;

        #endregion Public Events

        #region Public Properties

        // After stabilization
        public bool IsInResumeState => _isResuming;

        #endregion Public Properties

        #region Public Methods

        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;

        }

        /// <summary>
        /// Check if system recently resumed from sleep
        /// </summary>
        public bool IsRecentlyResumed()
        {
            return (DateTime.Now - _lastResumeTime).TotalSeconds < 10;
        }

        #endregion Public Methods

        #region Private Methods

        private void HandleResume()
        {


            _isResuming = true;
            _lastResumeTime = DateTime.Now;

            // Immediately notify that resume has started
            SystemResuming?.Invoke(this, EventArgs.Empty);

            // Schedule stabilization complete notification
            System.Threading.Tasks.Task.Delay(RESUME_STABILIZATION_DELAY_MS)
                .ContinueWith(_ =>
                {
                    _isResuming = false;

                    SystemResumed?.Invoke(this, EventArgs.Empty);
                });
        }

        private void HandleSuspend()
        {

            _isResuming = false;
            SystemSuspending?.Invoke(this, EventArgs.Empty);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {


            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    HandleSuspend();
                    break;

                case PowerModes.Resume:
                    HandleResume();
                    break;
            }
        }
        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {

            SessionEnding?.Invoke(this, EventArgs.Empty);
        }

        #endregion Private Methods
    }
}
