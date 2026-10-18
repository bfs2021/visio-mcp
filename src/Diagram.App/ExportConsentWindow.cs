using System.Windows;
using System.Windows.Controls;
using Diagram.Host.Core;

namespace Diagram.App;

/// <summary>
/// Asks the person whether a client may write a file somewhere it was not pre-approved to. It shows who is asking, the full path and the
/// action; closing the window, pressing Deny, or letting the wait run out all mean no.
/// </summary>
public sealed class ExportConsentWindow(Window owner) : IExportConsent
{
    public Task<bool> AskAsync(ConsentRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Dispatcher.InvokeAsync(() =>
        {
            var answer = false;
            var w = new Window
            {
                Title = "Allow this export?", Owner = owner.IsVisible ? owner : null, SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 640, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, ShowInTaskbar = true, Topmost = true,
            };
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 16, 16, 8), Text = $"{request.Reason}\n\nAction: {request.Action}\nFile: {request.Path}" };
            var allow = new Button { Content = "Allow", Width = 80, Margin = new Thickness(4), IsDefault = false };
            var deny = new Button { Content = "Deny", Width = 80, Margin = new Thickness(4), IsDefault = true, IsCancel = true };
            allow.Click += (_, _) => { answer = true; w.Close(); };
            deny.Click += (_, _) => { answer = false; w.Close(); };
            w.Content = new StackPanel { Children = { text, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8), Children = { allow, deny } } } };
            using var reg = ct.Register(() => owner.Dispatcher.BeginInvoke(() => { answer = false; if (w.IsLoaded) w.Close(); }));
            w.Closed += (_, _) => tcs.TrySetResult(answer);
            w.Show(); // not modal: the editor stays usable, and the answer is tied to the session checked afterwards
        });
        return tcs.Task;
    }
}
