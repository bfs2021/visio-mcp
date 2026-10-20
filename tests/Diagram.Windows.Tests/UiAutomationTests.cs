using System.Windows.Automation;
using System.Windows.Interop;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>W17: what a screen reader or any UI Automation client sees of the canvas in the real window.</summary>
public class UiAutomationTests
{
    [Fact]
    public async Task EachObjectOnThePageIsAListItemWithItsNameRolePositionAndSize()
    {
        await using var h = await Harness.StartAsync();
        Assert.Null((await h.Apply(0, null, Harness.Shape("alpha", 40, 40), Harness.Shape("beta", 200, 40))).Error);
        var hwnd = await h.OnUi(() => Task.FromResult(new WindowInteropHelper(h.Window).Handle));

        // UI Automation calls the window, so ask from a thread that is not the window's own
        var names = await Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var root = AutomationElement.FromHandle(hwnd);
                var items = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                var found = items.Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.Contains(", shape, at x ")).ToList();
                if (found.Count >= 2) return found;
                await Task.Delay(500);
            }
            return new List<string>();
        });

        Assert.Equal(2, names.Count);
        Assert.Contains(names, n => n.StartsWith("alpha, shape, at x ") && n.Contains(" by "));
        Assert.Contains(names, n => n.StartsWith("beta, shape, at x "));
    }
}
