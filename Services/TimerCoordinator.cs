using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace DeejNG.Services
{
    public class TimerCoordinator : IDisposable
    {
        #region Private Fields

        private DispatcherTimer _forceCleanupTimer;
        private DispatcherTimer _meterTimer;
        private DispatcherTimer _periodicPositionSaveTimer;
        private DispatcherTimer _positionSaveTimer;
        private DispatcherTimer _serialReconnectTimer;
        private DispatcherTimer _serialWatchdogTimer;
        private DispatcherTimer _sessionCacheTimer;

        #endregion Private Fields

        #region Public Events

        public event EventHandler ForceCleanup;

        public event EventHandler MeterUpdate;
        public event EventHandler PeriodicPositionSave;

        public event EventHandler PositionSave;

        public event EventHandler SerialReconnectAttempt;

        public event EventHandler SerialWatchdogCheck;

        public event EventHandler SessionCacheUpdate;

        #endregion Public Events

        #region Public Properties

        public bool IsMetersRunning => _meterTimer?.IsEnabled == true;
        public bool IsSerialReconnectRunning => _serialReconnectTimer?.IsEnabled == true;

        #endregion Public Properties

        #region Public Methods

        public void Dispose()
        {
            // Stop all timers and clear references to ensure proper cleanup
            StopAll();

            // Null out timer references to allow GC to collect them
            _meterTimer = null;
            _sessionCacheTimer = null;
            _forceCleanupTimer = null;
            _serialReconnectTimer = null;
            _serialWatchdogTimer = null;
            _positionSaveTimer = null;
            _periodicPositionSaveTimer = null;
        }

        public void InitializeTimers()
        {
            // Meter timer - high frequency for responsive meters
            _meterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(25) // 40 FPS for responsive meters
            };
            _meterTimer.Tick += (s, e) => MeterUpdate?.Invoke(s, e);

            // Session cache timer - moderate frequency for session management
            _sessionCacheTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(7) // Balance between performance and responsiveness
            };
            _sessionCacheTimer.Tick += (s, e) => SessionCacheUpdate?.Invoke(s, e);

            // Force cleanup timer - periodic maintenance
            _forceCleanupTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(5) // Frequent cleanup
            };
            _forceCleanupTimer.Tick += (s, e) => ForceCleanup?.Invoke(s, e);

            // Serial reconnect timer - for automatic reconnection
            _serialReconnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _serialReconnectTimer.Tick += (s, e) => SerialReconnectAttempt?.Invoke(s, e);

            // Serial watchdog timer - monitor connection health
            _serialWatchdogTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _serialWatchdogTimer.Tick += (s, e) => SerialWatchdogCheck?.Invoke(s, e);

            // Position save timer - debounce overlay position saves
            _positionSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _positionSaveTimer.Tick += (s, e) =>
            {
                _positionSaveTimer.Stop();
                PositionSave?.Invoke(s, e);
            };

            // Periodic position save timer - force save every 5 seconds if dirty
            _periodicPositionSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _periodicPositionSaveTimer.Tick += (s, e) => PeriodicPositionSave?.Invoke(s, e);
        }

        public void SetSerialReconnectInterval(TimeSpan interval)
        {
            if (_serialReconnectTimer != null)
            {
                _serialReconnectTimer.Interval = interval;
            }
        }

        public void StartAll()
        {
            StartSessionCache();
            StartForceCleanup();
            StartSerialWatchdog();
            // Note: Meters and serial reconnect are started conditionally
        }

        public void StartForceCleanup()
        {
            _forceCleanupTimer?.Start();
        }

        public void StartMeters()
        {
            _meterTimer?.Start();
        }

        public void StartPeriodicPositionSave()
        {
            _periodicPositionSaveTimer?.Start();
        }

        public void StartSerialReconnect()
        {
            if (_serialReconnectTimer != null)
            {

                _serialReconnectTimer.Start();
            }
        }

        public void StartSerialWatchdog()
        {
            _serialWatchdogTimer?.Start();
        }

        public void StartSessionCache()
        {
            _sessionCacheTimer?.Start();
        }

        public void StopAll()
        {
            StopMeters();
            StopSessionCache();
            StopForceCleanup();
            StopSerialReconnect();
            StopSerialWatchdog();
            _positionSaveTimer?.Stop();
            _periodicPositionSaveTimer?.Stop();
        }

        public void StopForceCleanup()
        {
            _forceCleanupTimer?.Stop();
        }

        public void StopMeters()
        {
            _meterTimer?.Stop();
        }
        public void StopPeriodicPositionSave()
        {
            _periodicPositionSaveTimer?.Stop();
        }

        public void StopSerialReconnect()
        {
            if (_serialReconnectTimer != null && _serialReconnectTimer.IsEnabled)
            {

                _serialReconnectTimer.Stop();
            }
        }

        public void StopSerialWatchdog()
        {
            _serialWatchdogTimer?.Stop();
        }

        public void StopSessionCache()
        {
            _sessionCacheTimer?.Stop();
        }
        public void TriggerPositionSave()
        {
            // Reset the timer - this cancels any pending save and starts a new countdown
            _positionSaveTimer?.Stop();
            _positionSaveTimer?.Start();
        }

        #endregion Public Methods
    }
}