using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// The exe's own icon from its Win32 resources. csc stores the /win32icon group under id 32512, and
// LoadImage picks the frame drawn for the requested size - the .ico has hand-made small frames,
// while Icon.ExtractAssociatedIcon would only scale the 32 px one down. Reading the resource also
// avoids embedding the icon file a second time as a managed resource.
static class AppIcon
{
    const int ExeIconResourceId = 32512;
    const uint ImageIcon = 1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int width, int height, uint flags);

    // Loaded once and kept for the life of the process on purpose: Icon.FromHandle does not own the
    // handle, the tray and the dialog share them, and Windows frees them when the process ends.
    public static readonly Icon Small = Load(SystemInformation.SmallIconSize);
    public static readonly Icon Large = Load(SystemInformation.IconSize);

    static Icon Load(Size size)
    {
        IntPtr handle = LoadImage(GetModuleHandle(null), new IntPtr(ExeIconResourceId), ImageIcon, size.Width, size.Height, 0);
        return handle == IntPtr.Zero ? SystemIcons.Application : Icon.FromHandle(handle);
    }
}
