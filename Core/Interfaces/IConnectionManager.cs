using System;

namespace DeejNG.Core.Interfaces
{
    /// <summary>
    /// Common abstraction over Serial and WebSocket connection managers.
    /// Allows MainWindow to subscribe to connection lifecycle events uniformly.
    /// </summary>
    public interface IConnectionManager : IDisposable
    {
        bool IsConnected { get; }

        /// <summary>Raised on the thread-pool; callers must Dispatcher.BeginInvoke to touch UI.</summary>
        event Action Connected;
        event Action Disconnected;
    }
}
