using System;
using System.Runtime.InteropServices;
using System.Threading;

// Watches the PC's keyboard for Ctrl+C pressed in a Remote Desktop window, for the On Demand mode,
// and reports when it was pressed the wanted number of times in a row.
//
// A global keyboard hook is what keyloggers use. So this one exists only while the user has switched
// On Demand on - with it off the exe has no keyboard hook at all - and it looks at nothing but
// Ctrl+C: of every other key it only notes whether Ctrl or Windows is held, and every key is passed
// on to the next hook unchanged.
//
// The hook lives on a thread of its own. Windows delivers low-level hook callbacks through the
// message loop of the thread that installed the hook, the keyboard waits for the answer meanwhile,
// and a hook that does not answer within LowLevelHooksTimeout is silently removed. The UI thread
// blocks in transfers and clipboard retries, so it cannot be that thread; this one does nothing but
// count and hand the result on.
sealed class CopyKeyWatcher : IDisposable
{
    const int KeyboardLowLevel = 13;
    const uint WmQuit = 0x0012;
    const uint PeekNoRemove = 0x0000;
    const int KeyC = 0x43;
    const int KeyLeftControl = 0xA2;
    const int KeyRightControl = 0xA3;
    const int KeyLeftWindows = 0x5B;
    const int KeyRightWindows = 0x5C;
    const int FlagAltDown = 0x20;
    const int FlagKeyUp = 0x80;
    // offsets in KBDLLHOOKSTRUCT
    const int KeyCodeOffset = 0;
    const int FlagsOffset = 8;

    delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr keyInfo);

    [StructLayout(LayoutKind.Sequential)]
    struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int hookType, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr keyInfo);

    [DllImport("user32.dll")]
    static extern int GetMessage(out NativeMessage message, IntPtr window, uint first, uint last);

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint first, uint last, uint remove);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string moduleName);

    readonly int pressesWanted;
    readonly Action<int> requested;
    // kept in a field: Windows only holds a native pointer to it, which does not keep it alive
    readonly LowLevelKeyboardProc callback;
    readonly CopyKeyPresses presses = new CopyKeyPresses();
    readonly ManualResetEvent hookThreadReady = new ManualResetEvent(false);
    readonly Thread hookThread;
    uint hookThreadId;

    // Only the hook thread touches these. The modifiers are tracked from the events this hook sees
    // rather than asked from the key state: a hook installed later - mstsc has one of its own - may
    // keep a key from ever reaching the key state.
    bool leftControl;
    bool rightControl;
    bool leftWindows;
    bool rightWindows;
    // the last foreground window looked at and whether it is a client's: one entry is enough, the
    // question only comes up at a Ctrl+C
    IntPtr lastWindow;
    bool lastWindowIsClient;

    // requested is called on the hook thread, with the Environment.TickCount of the press that
    // completed the count; it must hand on and return at once.
    public CopyKeyWatcher(int pressesWanted, Action<int> requested)
    {
        this.pressesWanted = pressesWanted;
        this.requested = requested;
        callback = OnKeyboardEvent;
        hookThread = new Thread(RunHookThread);
        hookThread.Name = "Ctrl+C watcher";
        // must not keep the process alive, whatever happens to Dispose
        hookThread.IsBackground = true;
        hookThread.Start();
        hookThreadReady.WaitOne();
    }

    public void Dispose()
    {
        PostThreadMessage(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        hookThread.Join(1000);
        hookThreadReady.Close();
    }

    void RunHookThread()
    {
        NativeMessage message;
        // creates the thread's message queue, without which Dispose could not post the quit to it
        PeekMessage(out message, IntPtr.Zero, 0, 0, PeekNoRemove);
        hookThreadId = GetCurrentThreadId();
        IntPtr hook = SetWindowsHookEx(KeyboardLowLevel, callback, GetModuleHandle(null), 0);
        hookThreadReady.Set();
        if (hook == IntPtr.Zero) return;

        while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(hook);
    }

    IntPtr OnKeyboardEvent(int code, IntPtr message, IntPtr keyInfo)
    {
        if (code >= 0)
        {
            // called straight from Windows, where nothing would catch what is thrown
            try { Look(Marshal.ReadInt32(keyInfo, KeyCodeOffset), Marshal.ReadInt32(keyInfo, FlagsOffset)); }
            catch (Exception) { }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, keyInfo);
    }

    void Look(int key, int flags)
    {
        bool down = (flags & FlagKeyUp) == 0;
        switch (key)
        {
            case KeyLeftControl: leftControl = down; break;
            case KeyRightControl: rightControl = down; break;
            case KeyLeftWindows: leftWindows = down; break;
            case KeyRightWindows: rightWindows = down; break;
            case KeyC:
                if (!down) presses.Released();
                else if (IsCopyInRemoteDesktop(flags)) CountPress();
                break;
        }
    }

    bool IsCopyInRemoteDesktop(int flags)
    {
        return (leftControl || rightControl) && (flags & FlagAltDown) == 0 && !leftWindows && !rightWindows
            && IsRemoteDesktopInFront();
    }

    // By process, not by window class or size: a full desktop session is one mstsc window, but with
    // RemoteApp every remote program is a window of its own on the local desktop, owned by mstsc.
    bool IsRemoteDesktopInFront()
    {
        IntPtr window = GetForegroundWindow();
        if (window != lastWindow)
        {
            lastWindow = window;
            lastWindowIsClient = RemoteDesktop.IsClientWindow(window);
        }
        return lastWindowIsClient;
    }

    void CountPress()
    {
        int tick = Environment.TickCount;
        int inARow = presses.CountPress(tick);
        if (inARow == 0) return;
        if (inARow == pressesWanted) requested(tick);
    }
}

// Counts Ctrl+C presses in a row. Holding the keys makes Windows repeat the key-down, which must not
// count again, so a new press needs the C key released in between.
//
// The time is passed in, in milliseconds of Environment.TickCount, so that the counting can be
// probed without waiting.
sealed class CopyKeyPresses
{
    // presses further apart than this are not "in a row" any more
    public const int MaxGapMilliseconds = 500;

    bool held;
    int inARow;
    int lastPressTick;

    // How many presses in a row this one makes, or 0 for a repeat of a key still held. The count
    // reaches any number only once per run, so comparing it fires once.
    public int CountPress(int tick)
    {
        if (held) return 0;
        held = true;

        // unchecked, so that the difference stays right when TickCount wraps around
        bool inTime = inARow > 0 && unchecked(tick - lastPressTick) <= MaxGapMilliseconds;
        inARow = inTime ? inARow + 1 : 1;
        lastPressTick = tick;
        return inARow;
    }

    public void Released()
    {
        held = false;
    }
}
