using System;
using System.Runtime.InteropServices;

// Reports whenever another window comes to the front - by notification, not by polling.
sealed class ForegroundWatcher : IDisposable
{
    const uint EventSystemForeground = 0x0003;
    // delivered through the message loop of the thread that asked, which is the only thread here
    const uint OutOfContext = 0x0000;
    const uint SkipOwnProcess = 0x0002;

    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint firstEvent, uint lastEvent, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    // kept in a field: Windows only holds a native pointer to it, which does not keep it alive
    readonly WinEventProc callback;
    readonly IntPtr hook;

    public ForegroundWatcher(Action foregroundChanged)
    {
        callback = delegate
        {
            // called straight from Windows, where nothing would catch what is thrown
            try { foregroundChanged(); }
            catch (Exception) { }
        };
        hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, callback, 0, 0, OutOfContext | SkipOwnProcess);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
    }
}
