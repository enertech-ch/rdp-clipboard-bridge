using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

static class SingleInstance
{
    const string MutexName = @"Local\RdpClipBridge_SingleInstance";
    const int TakeOverTimeoutMilliseconds = 10000;
    const int TakeOverPollMilliseconds = 200;
    static readonly IntPtr BroadcastToAllWindows = new IntPtr(0xFFFF);

    static readonly int ExitRequestMessage = (int)RegisterWindowMessage("RdpClipBridge_ExitRequest");

    // set when a newer copy asked this one to make way while it had nothing but a dialog to show
    public static bool ExitRequested;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    public static bool IsExitRequest(int message)
    {
        // a registration that failed yields 0, which is WM_NULL and must not close anything
        return ExitRequestMessage != 0 && message == ExitRequestMessage;
    }

    // Whoever created the named mutex is the running instance; the mutex is never waited on, only
    // held open, so an instance that crashes leaves nothing abandoned behind.
    public static Mutex TryAcquire()
    {
        bool createdNew;
        Mutex mutex = new Mutex(false, MutexName, out createdNew);
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }

    // The running copy's window is hidden but top-level, so a broadcast reaches it - as it reaches
    // the install dialog, which is all there is of a copy that was only just started. The mutex
    // disappears once its process has let go of it, which is the signal that it is gone.
    public static Mutex TakeOverFromRunningCopy()
    {
        PostMessage(BroadcastToAllWindows, ExitRequestMessage, IntPtr.Zero, IntPtr.Zero);
        Stopwatch waited = Stopwatch.StartNew();
        while (waited.ElapsedMilliseconds < TakeOverTimeoutMilliseconds)
        {
            Thread.Sleep(TakeOverPollMilliseconds);
            Mutex mutex = TryAcquire();
            if (mutex != null) return mutex;
        }
        return null;
    }
}
