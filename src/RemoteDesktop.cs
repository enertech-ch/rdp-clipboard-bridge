using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Tells Remote Desktop's clipboard and windows from everybody else's, by the process behind them.
static class RemoteDesktop
{
    // The Remote Desktop clients: mstsc, msrdc for Azure Virtual Desktop and the Windows App, and
    // vmconnect for Hyper-V. rdpclip, their counterpart inside a session, is deliberately not one of
    // them: a second copy of this tool running in the session would convert what this one hands
    // over - it arrives there as rdpclip's virtual files - and the two would pass the same files
    // back and forth without end.
    static readonly string[] Clients = { "mstsc", "msrdc", "vmconnect" };
    const string Explorer = "explorer";

    [DllImport("user32.dll")]
    static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    // Other programs use virtual files too - classic Outlook for a copied mail or attachment - and
    // put their own formats next to them, which replacing the clipboard would destroy: a mail could
    // no longer be pasted into another Outlook folder. So only Remote Desktop's copies are touched,
    // and a clipboard whose owner cannot be identified is left alone.
    public static bool OwnsClipboard()
    {
        string owner = ProcessNameBehind(GetClipboardOwner());
        return owner != null && IsClient(owner);
    }

    // True once a local app is in front. A client's window is the session itself. Explorer does not
    // count either: it pastes virtual files on its own, with their dates and without a detour, and
    // its taskbar and task switcher come to the front on the way from one window to another, which
    // is no reason yet to touch the clipboard.
    public static bool HasUserLeftSession()
    {
        string inFront = ProcessNameBehind(GetForegroundWindow());
        return inFront != null && !IsClient(inFront) && inFront != Explorer;
    }

    static bool IsClient(string processName)
    {
        return Array.IndexOf(Clients, processName) >= 0;
    }

    static string ProcessNameBehind(IntPtr window)
    {
        try
        {
            uint processId;
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out processId) == 0) return null;
            using (Process process = Process.GetProcessById((int)processId))
            {
                return process.ProcessName.ToLowerInvariant();
            }
        }
        catch (Exception)
        {
            // gone in the meantime
            return null;
        }
    }
}
