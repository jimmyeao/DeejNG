using System;

namespace DeejNG.Infrastructure.System
{
    public interface ISystemIntegrationService
    {
        #region Public Methods

        /// <summary>
        /// Disables the application from starting with Windows
        /// </summary>
        void DisableStartup();

        /// <summary>
        /// Enables the application to start with Windows
        /// </summary>
        void EnableStartup();
        /// <summary>
        /// Checks if the application is currently set to start with Windows
        /// </summary>
        /// <returns>True if startup is enabled, false otherwise</returns>
        bool IsStartupEnabled();

        /// <summary>
        /// Sets the display icon for the application in Programs and Features
        /// </summary>
        void SetDisplayIcon();

        #endregion Public Methods
    }
}
