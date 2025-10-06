using System;

namespace DeejNG.Core.Interfaces
{
    public interface IPowerManagementService : IDisposable
    {
        #region Public Events

        /// <summary>
        /// Fired when Windows session is ending (shutdown/logoff)
        /// </summary>
        event EventHandler SessionEnding;

        /// <summary>
        /// Fired after system has stabilized post-resume (2 seconds delay)
        /// </summary>
        event EventHandler SystemResumed;

        /// <summary>
        /// Fired immediately when system resumes from sleep
        /// </summary>
        event EventHandler SystemResuming;

        /// <summary>
        /// Fired when system is about to suspend
        /// </summary>
        event EventHandler SystemSuspending;

        #endregion Public Events

        #region Public Properties

        /// <summary>
        /// Returns true if system is currently in resume state
        /// </summary>
        bool IsInResumeState { get; }

        #endregion Public Properties

        #region Public Methods

        /// <summary>
        /// Check if system recently resumed from sleep (within 10 seconds)
        /// </summary>
        bool IsRecentlyResumed();

        #endregion Public Methods
    }
}
