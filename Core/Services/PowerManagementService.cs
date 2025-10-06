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
        private bool _isResuming = false;
        private DateTime _lastResumeTime = DateTime.MinValue;
        private const int RESUME_STABILIZATION_DELAY_MS = 2000; // 2 seconds
        
        public event EventHandler SystemSuspending;
        public event EventHandler SystemResuming;
        public event EventHandler SystemResumed; // After stabilization
        public event EventHandler SessionEnding;
        
        public bool IsInResumeState => _isResuming;

        public PowerManagementService()
        {
            // Register for Windows power events
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
            
#if DEBUG
            Debug.WriteLine("[PowerManagement] Service initialized");
#endif
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
#if DEBUG
            Debug.WriteLine($"[PowerManagement] Power mode changed: {e.Mode}");
#endif

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

        private void HandleSuspend()
        {
#if DEBUG
            Debug.WriteLine("[PowerManagement] System suspending - notifying components");
#endif
            _isResuming = false;
            SystemSuspending?.Invoke(this, EventArgs.Empty);
        }

        private void HandleResume()
        {
#if DEBUG
            Debug.WriteLine("[PowerManagement] System resuming - entering stabilization period");
#endif
            
            _isResuming = true;
            _lastResumeTime = DateTime.Now;
            
            // Immediately notify that resume has started
            SystemResuming?.Invoke(this, EventArgs.Empty);
            
            // Schedule stabilization complete notification
            System.Threading.Tasks.Task.Delay(RESUME_STABILIZATION_DELAY_MS)
                .ContinueWith(_ =>
                {
                    _isResuming = false;
#if DEBUG
                    Debug.WriteLine("[PowerManagement] Resume stabilization complete");
#endif
                    SystemResumed?.Invoke(this, EventArgs.Empty);
                });
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
#if DEBUG
            Debug.WriteLine($"[PowerManagement] Session ending: {e.Reason}");
#endif
            SessionEnding?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Check if system recently resumed from sleep
        /// </summary>
        public bool IsRecentlyResumed()
        {
            return (DateTime.Now - _lastResumeTime).TotalSeconds < 10;
        }

        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            
#if DEBUG
            Debug.WriteLine("[PowerManagement] Service disposed");
#endif
        }
    }
}
