using DeejNG.Core.Configuration;
using System.Configuration;
using System.Data;
using System.Windows;

namespace DeejNG
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Configure services
            ServiceLocator.Configure();
        }
        
        protected override void OnExit(ExitEventArgs e)
        {
            // Cleanup services
            ServiceLocator.Dispose();
            base.OnExit(e);
        }
    }

}
