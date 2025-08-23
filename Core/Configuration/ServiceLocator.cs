using DeejNG.Core.Interfaces;
using DeejNG.Core.Services;
using System;
using System.Collections.Generic;

namespace DeejNG.Core.Configuration
{
    /// <summary>
    /// Service locator for manual dependency injection until full DI container is implemented
    /// </summary>
    public static class ServiceLocator
    {
        private static readonly Dictionary<Type, object> _services = new();
        private static bool _isConfigured = false;

        public static void Configure()
        {
            if (_isConfigured) return;

            // Register services
            Register<IOverlayService>(new OverlayService());

            _isConfigured = true;
        }

        public static void Register<TInterface>(object implementation)
        {
            _services[typeof(TInterface)] = implementation;
        }

        public static T Get<T>()
        {
            if (_services.TryGetValue(typeof(T), out var service))
            {
                return (T)service;
            }
            throw new InvalidOperationException($"Service of type {typeof(T).Name} not registered");
        }

        public static void Dispose()
        {
            foreach (var service in _services.Values)
            {
                if (service is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _services.Clear();
            _isConfigured = false;
        }
    }
}
