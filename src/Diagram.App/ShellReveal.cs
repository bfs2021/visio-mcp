using System.IO;
using System.Runtime.InteropServices;

namespace Diagram.App;

/// <summary>
/// "Show in Explorer" for a file the person saved: asks the shell to open its folder with the file selected. This is the shell's own call, not a
/// program the application starts (the boundary guard keeps process launching out of the codebase), and it can only be reached for the saved
/// path of an open document.
/// </summary>
static class ShellReveal
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ILCreateFromPath(string path);
    [DllImport("shell32.dll")] static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[]? items, uint flags);
    [DllImport("shell32.dll")] static extern void ILFree(IntPtr pidl);

    public static void Reveal(string path)
    {
        var pidl = ILCreateFromPath(Path.GetFullPath(path));
        if (pidl == IntPtr.Zero) throw new IOException("the shell could not find that file");
        try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, null, 0)); }
        finally { ILFree(pidl); }
    }
}
