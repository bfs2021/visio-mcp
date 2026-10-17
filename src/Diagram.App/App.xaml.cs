using System.Windows;

namespace Diagram.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(AppOptions.Default());
        window.Show();
        await window.InitializeAsync();
        window.Navigate();
    }
}
