using Microsoft.Win32;

namespace Diagram.App;

/// <summary>
/// Lists the app under "Open with" for .json and .vsdx files, for the current user only (nothing under HKLM, no admin). Windows keys a file
/// association on the last extension alone, so a double extension such as .diagram.json cannot be claimed on its own: the app is offered for
/// .json files and is never made the default for them. A packaged installer (W16) can add a dedicated extension later.
/// </summary>
public static class FileAssociations
{
    public const string ExeKey = "Diagram.App.exe"; // must be the program file name: Windows matches Open with entries to the exe by it
    public static readonly string[] Extensions = [".json", ".vsdx"];

    /// <param name="classesRoot">Registry path that holds the per-user classes; tests pass a scratch path instead of Software\Classes.</param>
    public static void Register(string exePath, string classesRoot = @"Software\Classes")
    {
        using var app = Registry.CurrentUser.CreateSubKey($@"{classesRoot}\Applications\{ExeKey}");
        app.SetValue("FriendlyAppName", "Agentic Diagram");
        using (var types = app.CreateSubKey("SupportedTypes")) foreach (var ext in Extensions) types.SetValue(ext, "");
        using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exePath}\" \"%1\"");
        foreach (var ext in Extensions)
            using (var list = Registry.CurrentUser.CreateSubKey($@"{classesRoot}\{ext}\OpenWithList\{ExeKey}")) { }
    }

    public static void Unregister(string classesRoot = @"Software\Classes")
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{classesRoot}\Applications\{ExeKey}", throwOnMissingSubKey: false);
        foreach (var ext in Extensions) Registry.CurrentUser.DeleteSubKeyTree($@"{classesRoot}\{ext}\OpenWithList\{ExeKey}", throwOnMissingSubKey: false);
    }

    public static bool IsRegistered(string classesRoot = @"Software\Classes")
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\Applications\{ExeKey}\shell\open\command");
        return k?.GetValue("") is string;
    }
}
