using System.Windows;
using System.Windows.Threading;

namespace Sensor;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), e.Exception.GetType().FullName, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
