using System.IO;
using System.Windows;
using Diagram.Host.Core;
using Diagram.Ipc;

namespace Diagram.App;

public partial class App : Application
{
    InstanceGuard? guard;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var launch = LaunchRequest.Parse(e.Args);

        if (launch.Command is { } command)
        {
            // a one-off job for an installer or a person at a prompt: no window, and it never touches the running app
            try
            {
                var exe = Environment.ProcessPath ?? "";
                if (command == LaunchRequest.Register) FileAssociations.Register(exe); else FileAssociations.Unregister();
                Shutdown(0);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                MessageBox.Show($"Could not update the file associations: {ex.Message}", "Agentic Diagram", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
            }
            return;
        }

        var name = PipeNames.ForLaunch();
        guard = InstanceGuard.TryAcquire(name);
        if (guard is null)
        {
            // Another window of this user already runs. Hand it the files (or just ask it to come forward) and leave.
            if (!await InstanceGuard.ForwardAsync(name, [.. launch.Files.Select(f => f.Path)], TimeSpan.FromSeconds(8)))
            {
                MessageBox.Show("Agentic Diagram is already running but did not respond. Close it from the taskbar or Task Manager and try again.", "Agentic Diagram", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
            }
            else Shutdown(0);
            return;
        }

        var window = new MainWindow(AppOptions.Default());
        window.Closed += async (_, _) => { await window.ShutdownCompleted; await guard.DisposeAsync(); };
        window.Show();
        await window.InitializeAsync();
        window.Navigate();
        guard.Attach(async paths => await Report(window, await window.OpenFromShellAsync(LaunchRequest.FromForwarded(paths))));
        if (launch.Files.Count > 0 || launch.Skipped.Count > 0) await Report(window, await window.OpenFromShellAsync(launch));
    }

    static Task Report(MainWindow window, IReadOnlyList<string> problems) =>
        problems.Count == 0 ? Task.CompletedTask
            : window.Dispatcher.InvokeAsync(() => MessageBox.Show(window, "Some files were not opened:\n\n" + string.Join("\n", problems), "Agentic Diagram", MessageBoxButton.OK, MessageBoxImage.Information)).Task;
}
