// Sits in the tray and turns files copied inside a Remote Desktop session into real files, so they
// can be pasted into apps that only accept a plain file list - New Outlook and other
// Chromium/WebView2 apps.
//
// mstsc offers copied remote files as virtual files (FileGroupDescriptorW + FileContents) that are
// only transferred when a paste asks for them. Chromium ignores those formats and takes nothing but
// CF_HDROP, so Ctrl+V does nothing there. This downloads the files into %TEMP%\RDPClip right after
// the copy and, once the user turns to a local app, puts that file list on the clipboard instead.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using ComTypes = System.Runtime.InteropServices.ComTypes;

class RdpClipboardBridge : Form
{
    const int WmClipboardUpdate = 0x031D;
    const int BalloonMilliseconds = 5000;
    const int MaxTrayTextLength = 63;
    const int ExtensionsShownInMenu = 4;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RemoveClipboardFormatListener(IntPtr window);

    readonly NotifyIcon trayIcon = new NotifyIcon();
    readonly ForegroundWatcher foregroundWatcher;
    readonly ToolStripMenuItem pauseMenuItem;
    readonly ToolStripMenuItem startWithWindowsMenuItem;
    readonly ToolStripMenuItem fileTypesMenuItem;
    readonly ToolStripMenuItem sizeLimitMenuItem;
    readonly ToolStripMenuItem pauseOnMeteredMenuItem;
    bool paused;
    // why the copy on the clipboard was left to Remote Desktop, for the tray tooltip
    LeftAloneReason leftAlone;
    bool bridging;
    bool closeWhenIdle;
    bool foregroundChanged;
    bool dialogOpen;
    bool keepMenuOpen;
    uint clipboardUpdates;
    uint updatesLookedAt;
    // downloaded, and waiting for the user to turn to a local app
    StagedCopy staged;

    // set when this instance closes so that the installed copy takes over
    public string HandOverTo { get; private set; }

    [STAThread]
    static void Main()
    {
        // an unattended background app must never end with an error dialog
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate { };
        Application.EnableVisualStyles();
        // GDI text rendering instead of GDI+, which measures and shapes non-Latin scripts poorly -
        // and Chinese, Japanese and Russian are among the shipped translations
        Application.SetCompatibleTextRenderingDefault(false);

        bool replacingInstalledCopy = false;
        Mutex singleInstance = SingleInstance.TryAcquire();
        if (singleInstance == null)
        {
            if (Installation.IsRunningInstalled) return;
            replacingInstalledCopy = Installation.IsInstalledCopyRunning();
            if (!InstallFlow.ConfirmReplacingRunningCopy(replacingInstalledCopy)) return;
            singleInstance = SingleInstance.TakeOverFromRunningCopy();
            if (singleInstance == null)
            {
                InstallFlow.ReportRunningCopyDidNotExit();
                return;
            }
        }

        string handOverTo = null;
        try
        {
            if (replacingInstalledCopy) handOverTo = InstallFlow.Upgrade();
            else if (ShouldOfferInstall()) handOverTo = InstallFlow.Offer();
            // a newer copy may have asked this one to go while it was still asking its question
            if (handOverTo == null && !SingleInstance.ExitRequested) handOverTo = RunInTray();
        }
        finally
        {
            // released before the installed copy starts, or that copy would find it and quit
            singleInstance.Dispose();
        }
        if (handOverTo != null) InstallFlow.Start(handOverTo);
    }

    static bool ShouldOfferInstall()
    {
        return !Installation.IsRunningInstalled && !Settings.IsOn(Settings.InstallDeclined);
    }

    static string RunInTray()
    {
        using (RdpClipboardBridge bridge = new RdpClipboardBridge())
        {
            Application.Run(bridge);
            return bridge.HandOverTo;
        }
    }

    RdpClipboardBridge()
    {
        pauseMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuPause), null, OnPauseClicked);
        startWithWindowsMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuStartWithWindows), null, OnStartWithWindowsClicked);
        fileTypesMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuFileTypes));
        foreach (FileTypeGroup fileType in FileTypes.All) fileTypesMenuItem.DropDownItems.Add(NewFileTypeMenuItem(fileType));
        sizeLimitMenuItem = new ToolStripMenuItem(SizeLimitMenuText(), null, OnSizeLimitClicked);
        pauseOnMeteredMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuPauseOnMetered), null, OnPauseOnMeteredClicked);
        ToolStripMenuItem installMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuInstall), null, OnInstallClicked);
        ToolStripMenuItem uninstallMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuUninstall), null, OnUninstallClicked);
        installMenuItem.Visible = !Installation.IsRunningInstalled;
        uninstallMenuItem.Visible = Installation.IsRunningInstalled;

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add(pauseMenuItem);
        menu.Items.Add(startWithWindowsMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(fileTypesMenuItem);
        menu.Items.Add(sizeLimitMenuItem);
        menu.Items.Add(pauseOnMeteredMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Texts.Get(TextId.MenuOpenTempFolder), null, OnOpenTempFolderClicked);
        menu.Items.Add(installMenuItem);
        menu.Items.Add(uninstallMenuItem);
        menu.Items.Add(Texts.Get(TextId.MenuExit), null, OnExitClicked);
        menu.Opening += OnMenuOpening;
        KeepOpenWhileTickingFileTypes(menu);

        trayIcon.Icon = AppIcon.Small;
        trayIcon.ContextMenuStrip = menu;
        UpdateTrayText();
        trayIcon.Visible = true;

        foregroundWatcher = new ForegroundWatcher(OnForegroundChanged);
    }

    ToolStripMenuItem NewFileTypeMenuItem(FileTypeGroup fileType)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(Texts.Get(fileType.Label), null, OnFileTypeClicked);
        item.Tag = fileType;
        // the place of a keyboard shortcut is free here, and a few extensions say more than the label
        item.ShortcutKeyDisplayString = fileType.Examples(ExtensionsShownInMenu);
        return item;
    }

    // A click closes the whole menu, which would mean opening it again for every file type to tick.
    // The click is reported before the menu is told to close, so that is where it is decided - for
    // both levels, because the close runs through the submenu and the menu above it.
    void KeepOpenWhileTickingFileTypes(ContextMenuStrip menu)
    {
        menu.ItemClicked += delegate { keepMenuOpen = false; };
        fileTypesMenuItem.DropDown.ItemClicked += delegate { keepMenuOpen = true; };
        menu.Closing += OnMenuClosing;
        fileTypesMenuItem.DropDown.Closing += OnMenuClosing;
    }

    void OnMenuClosing(object sender, ToolStripDropDownClosingEventArgs e)
    {
        if (keepMenuOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
    }

    // Application.Run makes its form visible; refusing keeps the window off the screen, the taskbar
    // and Alt+Tab, while the handle the clipboard listener needs is still created. ShowInTaskbar is
    // deliberately left alone: turning it off gives the window a hidden owner, and the exit request
    // of an upgrade is a broadcast, which is only documented to reach unowned hidden windows.
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) CreateHandle();
        base.SetVisibleCore(false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    // Closing in the middle of a transfer would dispose the window under the running COM call,
    // whoever asks for it - the tray menu, a newer copy taking over, taskkill. The transfer is given
    // up instead and the close follows it. Only Windows shutting down is not kept waiting.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (bridging && e.CloseReason != CloseReason.WindowsShutDown)
        {
            closeWhenIdle = true;
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foregroundWatcher.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (SingleInstance.IsExitRequest(message.Msg))
        {
            Close();
            return;
        }
        if (message.Msg == WmClipboardUpdate)
        {
            clipboardUpdates++;
            Bridge();
        }
        base.WndProc(ref message);
    }

    void OnForegroundChanged()
    {
        foregroundChanged = true;
        Bridge();
    }

    // Every question put to the clipboard is a COM call into mstsc, and COM keeps dispatching window
    // messages while it waits - so the next clipboard update or change of window arrives in the
    // middle of this. Those are only noted, and the pass that is running goes round again for them.
    void Bridge()
    {
        if (bridging) return;

        bridging = true;
        try
        {
            while (!paused && !closeWhenIdle && (clipboardUpdates != updatesLookedAt || foregroundChanged))
            {
                TryBridgeOnce();
            }
        }
        finally
        {
            bridging = false;
        }
        if (closeWhenIdle) Close();
    }

    void TryBridgeOnce()
    {
        try
        {
            if (clipboardUpdates != updatesLookedAt)
            {
                updatesLookedAt = clipboardUpdates;
                StageClipboard();
            }
            foregroundChanged = false;
            HandOverStagedCopy();
        }
        catch (Exception exception)
        {
            ShowWarning(Texts.Get(TextId.WarningPrepareFailed, exception.Message));
        }
    }

    // Downloads what was copied in the session, without touching the clipboard yet.
    void StageClipboard()
    {
        if (!VirtualFileClipboard.HasVirtualFilesOnly() || !RemoteDesktop.OwnsClipboard())
        {
            LeaveAlone(LeftAloneReason.None);
            return;
        }

        // Converting transfers every copied file right away, pasted or not. Left alone, Remote
        // Desktop transfers only what is pasted - which is what a metered connection wants.
        if (Settings.IsOn(Settings.PauseOnMetered) && NetworkCost.IsMetered())
        {
            LeaveAlone(LeftAloneReason.MeteredConnection);
            return;
        }

        StagedCopy copy = VirtualFileClipboard.Stage(staged, IsAbandoned);
        // the same copy, announced once more
        if (copy == staged) return;
        staged = copy.IsWorthKeeping ? copy : null;
        ShowLeftAlone(copy.LeftAlone);
    }

    void LeaveAlone(LeftAloneReason reason)
    {
        staged = null;
        ShowLeftAlone(reason);
    }

    // The clipboard is only replaced once the user turns to a local app, because replacing it is not
    // a local affair: mstsc announces every local change back to the session, where this PC's file
    // list then takes the place of what was copied there. A cut would no longer move anything, a
    // paste inside the session would send the files to this PC and back, and a mail copied in the
    // session's Outlook would lose the formats Outlook needs to paste it. As long as the user stays
    // in the session, nothing of that happens.
    void HandOverStagedCopy()
    {
        if (staged == null || !RemoteDesktop.HasUserLeftSession()) return;

        StagedCopy copy = staged;
        staged = null;
        // something else was copied since - don't overwrite it
        if (!VirtualFileClipboard.StillHolds(copy.CopiedFiles)) return;

        // only now, since a copy that is pasted inside the session never needed these files
        if (copy.FailedCount > 0) ShowWarning(Texts.Get(TextId.WarningFilesFailed, copy.FailedCount));
        if (copy.TopLevelPaths.Count > 0) PutFileDropList(copy.TopLevelPaths);
    }

    // A transfer nobody waits for any more is given up: the app is closing or was paused, or
    // something else was copied meanwhile. An update alone does not say the latter - the same copy is
    // announced again at times - only what the clipboard holds does, and that is asked once per
    // update rather than once per chunk.
    bool IsAbandoned(string copiedFiles)
    {
        if (closeWhenIdle || paused) return true;
        if (clipboardUpdates == updatesLookedAt) return false;

        uint updates = clipboardUpdates;
        try
        {
            // leaves the update open, so that the pass looks at the clipboard again afterwards
            if (!VirtualFileClipboard.StillHolds(copiedFiles)) return true;
            updatesLookedAt = updates;
        }
        catch (ExternalException)
        {
            // the clipboard is busy right now; the next chunk asks again
        }
        return false;
    }

    static void PutFileDropList(List<string> paths)
    {
        StringCollection fileDropList = new StringCollection();
        fileDropList.AddRange(paths.ToArray());
        // retries on its own while mstsc still holds the clipboard open
        Clipboard.SetFileDropList(fileDropList);
    }

    void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // the tray icon stays clickable behind an open dialog, and a second one on top helps nobody
        if (dialogOpen)
        {
            e.Cancel = true;
            return;
        }

        keepMenuOpen = false;
        startWithWindowsMenuItem.Checked = Installation.IsAutostartFor(Installation.CurrentExePath);
        pauseOnMeteredMenuItem.Checked = Settings.IsOn(Settings.PauseOnMetered);
        foreach (ToolStripMenuItem item in fileTypesMenuItem.DropDownItems)
        {
            item.Checked = ((FileTypeGroup)item.Tag).IsConverted;
        }
        sizeLimitMenuItem.Text = SizeLimitMenuText();
    }

    static string SizeLimitMenuText()
    {
        return Texts.Get(TextId.MenuSizeLimit, SizeLimit.Megabytes);
    }

    // The switches below apply from the next copy; what is on the clipboard now stays as it is.
    void OnFileTypeClicked(object sender, EventArgs e)
    {
        ToolStripMenuItem item = (ToolStripMenuItem)sender;
        FileTypeGroup fileType = (FileTypeGroup)item.Tag;
        Settings.Set(fileType.SettingName, !item.Checked);
        // the menu stays open, so the tick follows right here - and shows what was really stored
        item.Checked = fileType.IsConverted;
    }

    void OnSizeLimitClicked(object sender, EventArgs e)
    {
        int? megabytes = WhileDialogOpen<int?>(delegate { return SizeLimitDialog.Ask(SizeLimit.Megabytes); });
        if (!megabytes.HasValue) return;
        SizeLimit.Set(megabytes.Value);
        // the tooltip would otherwise name the new limit for a copy judged by the old one
        if (leftAlone == LeftAloneReason.SizeLimit) ShowLeftAlone(LeftAloneReason.None);
    }

    void OnPauseOnMeteredClicked(object sender, EventArgs e)
    {
        Settings.Set(Settings.PauseOnMetered, !pauseOnMeteredMenuItem.Checked);
        if (leftAlone == LeftAloneReason.MeteredConnection) ShowLeftAlone(LeftAloneReason.None);
    }

    // Without a hint the tray would give no clue why the copied files do not paste this time. Only
    // the tooltip says it, not a balloon: most copies are pasted inside the session and need no
    // comment.
    void ShowLeftAlone(LeftAloneReason reason)
    {
        if (leftAlone == reason) return;
        leftAlone = reason;
        UpdateTrayText();
    }

    // What was copied before or during a pause stays as it is; converting resumes with the next copy.
    void OnPauseClicked(object sender, EventArgs e)
    {
        paused = !paused;
        staged = null;
        updatesLookedAt = clipboardUpdates;
        pauseMenuItem.Text = Texts.Get(paused ? TextId.MenuResume : TextId.MenuPause);
        UpdateTrayText();
    }

    void OnStartWithWindowsClicked(object sender, EventArgs e)
    {
        bool enable = !startWithWindowsMenuItem.Checked;
        try
        {
            if (enable) Installation.EnableAutostart(Installation.CurrentExePath);
            else Installation.DisableAutostart();
        }
        catch (Exception exception)
        {
            ShowWarning(Texts.Get(TextId.WarningAutostartFailed, exception.Message));
        }
    }

    void OnOpenTempFolderClicked(object sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(StagingFolder.Location);
            Process.Start("explorer.exe", "\"" + StagingFolder.Location + "\"");
        }
        catch (Exception exception)
        {
            ShowWarning(Texts.Get(TextId.WarningOpenFolderFailed, StagingFolder.Location, exception.Message));
        }
    }

    void OnInstallClicked(object sender, EventArgs e)
    {
        string installedExe = WhileDialogOpen<string>(InstallFlow.Offer);
        if (installedExe == null) return;
        HandOverTo = installedExe;
        Close();
    }

    void OnUninstallClicked(object sender, EventArgs e)
    {
        if (WhileDialogOpen<bool>(InstallFlow.ConfirmAndUninstall)) Close();
    }

    void OnExitClicked(object sender, EventArgs e)
    {
        Close();
    }

    T WhileDialogOpen<T>(Func<T> dialog)
    {
        dialogOpen = true;
        try
        {
            return dialog();
        }
        finally
        {
            dialogOpen = false;
        }
    }

    void UpdateTrayText()
    {
        string text = Texts.ProductName;
        if (paused) text = Texts.Get(TextId.TrayTooltipPaused, Texts.ProductName);
        else if (leftAlone == LeftAloneReason.MeteredConnection) text = Texts.Get(TextId.TrayTooltipMetered, Texts.ProductName);
        else if (leftAlone == LeftAloneReason.FileType) text = Texts.Get(TextId.TrayTooltipFileType, Texts.ProductName);
        else if (leftAlone == LeftAloneReason.SizeLimit) text = Texts.Get(TextId.TrayTooltipSizeLimit, Texts.ProductName, SizeLimit.Megabytes);
        // NotifyIcon throws on anything longer, and a translation may well be
        trayIcon.Text = text.Length > MaxTrayTextLength ? text.Substring(0, MaxTrayTextLength) : text;
    }

    void ShowWarning(string text)
    {
        trayIcon.ShowBalloonTip(BalloonMilliseconds, Texts.ProductName, text, ToolTipIcon.Warning);
    }
}

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

// A copy that was downloaded into the staging folder.
class StagedCopy
{
    // names, sizes and dates of what the clipboard announced, to recognise the same copy again
    public readonly string CopiedFiles;
    // What goes on the clipboard: a copied folder is listed once, and whoever pastes it takes
    // everything below it along - listing its files as well would paste them twice.
    public readonly List<string> TopLevelPaths = new List<string>();
    public int FailedCount;
    // given up half way, because nobody was waiting for it any more
    public bool Abandoned;
    // not downloaded at all, but left to Remote Desktop
    public LeftAloneReason LeftAlone;

    public StagedCopy(string copiedFiles)
    {
        CopiedFiles = copiedFiles;
    }

    // A copy whose files all failed is kept too: its warning is still owed once the user wants it.
    public bool IsWorthKeeping
    {
        get { return !Abandoned && (TopLevelPaths.Count > 0 || FailedCount > 0); }
    }
}

class VirtualFile
{
    public int DescriptorIndex;
    public string RemoteName;
    public bool IsFolder;
    public long KnownSize = -1;
    public DateTime? LastWriteTimeUtc;
}

// The clipboard's data object together with the file list it announces.
sealed class ClipboardFiles : IDisposable
{
    public readonly ComTypes.IDataObject Data;
    public readonly List<VirtualFile> Files;

    public ClipboardFiles(ComTypes.IDataObject data, List<VirtualFile> files)
    {
        Data = data;
        Files = files;
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(Data);
    }
}

sealed class TransferAbandonedException : Exception
{
}

static class VirtualFileClipboard
{
    const int CfHdrop = 15;
    static readonly int FileGroupDescriptorFormat = DataFormats.GetFormat("FileGroupDescriptorW").Id;
    static readonly int FileContentsFormat = DataFormats.GetFormat("FileContents").Id;

    // FILEGROUPDESCRIPTORW: a uint32 count, followed by that many FILEDESCRIPTORW records
    const int DescriptorCountSize = 4;
    const int FileDescriptorSize = 592;
    const int FlagsOffset = 0;
    const int AttributesOffset = 36;
    const int LastWriteTimeOffset = 56;
    const int FileSizeHighOffset = 64;
    const int FileSizeLowOffset = 68;
    const int FileNameOffset = 72;
    const int MaxFileNameChars = 260;
    const uint FdAttributes = 0x4;
    const uint FdWritesTime = 0x20;
    const uint FdFileSize = 0x40;
    const uint FileAttributeDirectory = 0x10;

    const int CopyChunkSize = 81920;
    const int WholeObject = -1;

    [DllImport("user32.dll")]
    static extern bool IsClipboardFormatAvailable(int format);

    [DllImport("ole32.dll")]
    static extern int OleGetClipboard(out ComTypes.IDataObject dataObject);

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern UIntPtr GlobalSize(IntPtr memory);

    // A real file list on the clipboard already works everywhere, including our own replacement.
    public static bool HasVirtualFilesOnly()
    {
        return IsClipboardFormatAvailable(FileGroupDescriptorFormat) && !IsClipboardFormatAvailable(CfHdrop);
    }

    // Downloads what the clipboard announces into a new batch folder. Returns alreadyStaged itself
    // when that is what the clipboard still holds: the same copy must not be transferred twice.
    public static StagedCopy Stage(StagedCopy alreadyStaged, Func<string, bool> isAbandoned)
    {
        using (ClipboardFiles clipboard = ReadClipboardFiles())
        {
            string copiedFiles = Describe(clipboard.Files);
            if (alreadyStaged != null && alreadyStaged.CopiedFiles == copiedFiles) return alreadyStaged;

            StagedCopy copy = new StagedCopy(copiedFiles);
            if (clipboard.Files.Count == 0) return copy;
            copy.LeftAlone = CopyFilter.ReasonToLeaveAlone(clipboard.Files);
            if (copy.LeftAlone != LeftAloneReason.None) return copy;

            Func<bool> abandoned = delegate { return isAbandoned(copiedFiles); };
            string batchFolder = StagingFolder.StartNewBatch();
            try
            {
                foreach (VirtualFile file in clipboard.Files)
                {
                    string topLevelPath = TryExtract(clipboard.Data, file, batchFolder, abandoned);
                    if (topLevelPath == null) copy.FailedCount++;
                    else if (!ContainsPath(copy.TopLevelPaths, topLevelPath)) copy.TopLevelPaths.Add(topLevelPath);
                }
            }
            catch (TransferAbandonedException)
            {
                copy.Abandoned = true;
            }
            return copy;
        }
    }

    // Compared by content, because whether reading the files moves the clipboard sequence number is
    // up to mstsc, and a wrong guess there would mean nothing is ever put on the clipboard.
    public static bool StillHolds(string copiedFiles)
    {
        if (!HasVirtualFilesOnly()) return false;
        using (ClipboardFiles clipboard = ReadClipboardFiles())
        {
            return Describe(clipboard.Files) == copiedFiles;
        }
    }

    // Getting the clipboard's data object and asking it for the file list are retried as one step:
    // either of them can find mstsc still holding the clipboard open.
    static ClipboardFiles ReadClipboardFiles()
    {
        return ClipboardRetry.Run<ClipboardFiles>(delegate
        {
            ComTypes.IDataObject data = OpenOleClipboard();
            try
            {
                return new ClipboardFiles(data, ParseFileGroupDescriptor(ReadWholeFormat(data, FileGroupDescriptorFormat)));
            }
            catch (Exception)
            {
                Marshal.ReleaseComObject(data);
                throw;
            }
        });
    }

    // Parsed fields rather than the raw descriptor, whose unused bytes need not be the same twice.
    // The date is part of it so that a file changed and copied again is not taken for the old one.
    static string Describe(List<VirtualFile> files)
    {
        StringBuilder description = new StringBuilder();
        foreach (VirtualFile file in files)
        {
            description.Append(file.DescriptorIndex).Append('|').Append(file.RemoteName).Append('|');
            description.Append(file.KnownSize).Append('|').Append(file.LastWriteTimeUtc.GetValueOrDefault().Ticks).Append('\n');
        }
        return description.ToString();
    }

    // Straight from ole32 instead of Clipboard.GetDataObject: FileContents has to be requested per
    // file through FORMATETC.lindex, which WinForms' DataObject API has no parameter for. Talking to
    // the OLE object directly makes sure the index reaches mstsc rather than relying on WinForms
    // internals forwarding it.
    static ComTypes.IDataObject OpenOleClipboard()
    {
        ComTypes.IDataObject clipboardData;
        Marshal.ThrowExceptionForHR(OleGetClipboard(out clipboardData));
        return clipboardData;
    }

    static byte[] ReadWholeFormat(ComTypes.IDataObject data, int format)
    {
        using (MemoryStream buffer = new MemoryStream())
        {
            ReadFormat(data, format, WholeObject, buffer, -1, null);
            return buffer.ToArray();
        }
    }

    static List<VirtualFile> ParseFileGroupDescriptor(byte[] descriptor)
    {
        List<VirtualFile> files = new List<VirtualFile>();
        if (descriptor.Length < DescriptorCountSize) return files;

        uint count = BitConverter.ToUInt32(descriptor, 0);
        for (int index = 0; index < count; index++)
        {
            int offset = DescriptorCountSize + index * FileDescriptorSize;
            if (offset + FileDescriptorSize > descriptor.Length) break;

            VirtualFile file = ParseFileDescriptor(descriptor, offset);
            if (file == null) continue;
            // lindex addresses the descriptor array, so entries without a name still count
            file.DescriptorIndex = index;
            files.Add(file);
        }
        return files;
    }

    // Folders are listed as entries of their own, with the files below them named "folder\file".
    static VirtualFile ParseFileDescriptor(byte[] descriptor, int offset)
    {
        uint flags = BitConverter.ToUInt32(descriptor, offset + FlagsOffset);
        uint attributes = BitConverter.ToUInt32(descriptor, offset + AttributesOffset);

        string name = Encoding.Unicode.GetString(descriptor, offset + FileNameOffset, MaxFileNameChars * 2);
        int terminator = name.IndexOf('\0');
        if (terminator >= 0) name = name.Substring(0, terminator);
        if (name.Length == 0) return null;

        VirtualFile file = new VirtualFile();
        file.RemoteName = name;
        file.IsFolder = (flags & FdAttributes) != 0 && (attributes & FileAttributeDirectory) != 0;
        if ((flags & FdFileSize) != 0)
        {
            long high = BitConverter.ToUInt32(descriptor, offset + FileSizeHighOffset);
            long low = BitConverter.ToUInt32(descriptor, offset + FileSizeLowOffset);
            file.KnownSize = (high << 32) | low;
        }
        if ((flags & FdWritesTime) != 0)
        {
            file.LastWriteTimeUtc = ToDateTime(BitConverter.ToInt64(descriptor, offset + LastWriteTimeOffset));
        }
        return file;
    }

    // A date that cannot be represented is treated as no date.
    static DateTime? ToDateTime(long fileTime)
    {
        try
        {
            if (fileTime <= 0) return null;
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // Returns the top-level file or folder the entry ended up in, or null - a failed file must not
    // be left behind half written.
    static string TryExtract(ComTypes.IDataObject data, VirtualFile file, string batchFolder, Func<bool> abandoned)
    {
        string path = null;
        try
        {
            if (abandoned()) throw new TransferAbandonedException();

            string relativePath = StagingFolder.ToLocalRelativePath(file.RemoteName);
            string target = Path.Combine(batchFolder, relativePath);
            if (relativePath.Length == 0 || !StagingFolder.IsInside(batchFolder, target)) return null;

            string topLevelPath = Path.Combine(batchFolder, relativePath.Split('\\')[0]);
            if (file.IsFolder)
            {
                // has no contents to fetch, but an empty folder would be lost without its own entry
                Directory.CreateDirectory(target);
                return topLevelPath;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            path = StagingFolder.FreePathFor(target);
            using (FileStream contents = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                ReadFormat(data, FileContentsFormat, file.DescriptorIndex, contents, file.KnownSize, abandoned);
            }
            TryStampLastWriteTime(path, file.LastWriteTimeUtc);
            return relativePath.IndexOf('\\') < 0 ? path : topLevelPath;
        }
        catch (TransferAbandonedException)
        {
            StagingFolder.TryDeleteFile(path);
            throw;
        }
        catch (Exception)
        {
            // whatever went wrong - transfer broken off, disk full, unusable name - skip just this file
            StagingFolder.TryDeleteFile(path);
            return null;
        }
    }

    // A pasted file should carry the date it has on the remote machine, not the moment it was
    // downloaded - sorting by date and tools that compare dates rely on it. Not worth failing for.
    static void TryStampLastWriteTime(string path, DateTime? lastWriteTimeUtc)
    {
        if (!lastWriteTimeUtc.HasValue) return;
        try { File.SetLastWriteTimeUtc(path, lastWriteTimeUtc.Value); }
        catch (Exception) { }
    }

    static bool ContainsPath(List<string> paths, string path)
    {
        foreach (string known in paths)
        {
            if (string.Equals(known, path, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static void ReadFormat(ComTypes.IDataObject data, int format, int index, Stream target, long knownSize, Func<bool> abandoned)
    {
        ComTypes.FORMATETC request = new ComTypes.FORMATETC();
        request.cfFormat = unchecked((short)format);
        request.dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT;
        request.lindex = index;
        request.ptd = IntPtr.Zero;
        request.tymed = ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL;

        ComTypes.STGMEDIUM medium;
        data.GetData(ref request, out medium);
        try
        {
            if (medium.tymed == ComTypes.TYMED.TYMED_ISTREAM) CopyComStream(medium.unionmember, target, knownSize, abandoned);
            else if (medium.tymed == ComTypes.TYMED.TYMED_HGLOBAL) CopyGlobalMemory(medium.unionmember, target, knownSize);
            else throw new NotSupportedException("unexpected storage medium " + medium.tymed);
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    static void CopyComStream(IntPtr streamPointer, Stream target, long knownSize, Func<bool> abandoned)
    {
        ComTypes.IStream source = (ComTypes.IStream)Marshal.GetObjectForIUnknown(streamPointer);
        IntPtr bytesReadPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            TryRewind(source);
            byte[] chunk = new byte[CopyChunkSize];
            long copied = 0;
            while (true)
            {
                if (abandoned != null && abandoned()) throw new TransferAbandonedException();

                // a stream that leaves the count alone must not have the last one counted again
                Marshal.WriteInt32(bytesReadPointer, 0);
                source.Read(chunk, chunk.Length, bytesReadPointer);
                int bytesRead = Marshal.ReadInt32(bytesReadPointer);
                if (bytesRead <= 0) break;
                target.Write(chunk, 0, bytesRead);
                copied += bytesRead;
            }
            ThrowIfCutShort(copied, knownSize);
        }
        finally
        {
            Marshal.FreeCoTaskMem(bytesReadPointer);
            Marshal.ReleaseComObject(source);
        }
    }

    // A transfer that breaks off can end like a complete one, without any error - and the cut-off
    // file would be attached to a mail as if it were whole. More than announced is fine: a log file
    // may have grown since it was copied.
    static void ThrowIfCutShort(long copied, long knownSize)
    {
        if (knownSize >= 0 && copied < knownSize) throw new EndOfStreamException();
    }

    // Some sources hand out the stream positioned at its end, which would read as an empty file.
    // Streams that cannot seek start at the beginning anyway.
    static void TryRewind(ComTypes.IStream stream)
    {
        try { stream.Seek(0, 0, IntPtr.Zero); }
        catch (Exception) { }
    }

    // GlobalSize may round the block up, so the descriptor's size wins when it has one.
    static void CopyGlobalMemory(IntPtr memory, Stream target, long knownSize)
    {
        long length = (long)GlobalSize(memory).ToUInt64();
        ThrowIfCutShort(length, knownSize);
        if (knownSize >= 0) length = knownSize;

        IntPtr source = GlobalLock(memory);
        if (source == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            byte[] chunk = new byte[CopyChunkSize];
            for (long copied = 0; copied < length; copied += chunk.Length)
            {
                int count = (int)Math.Min(chunk.Length, length - copied);
                Marshal.Copy(new IntPtr(source.ToInt64() + copied), chunk, 0, count);
                target.Write(chunk, 0, count);
            }
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }
}

// Decides whether a copy is converted at all. It is taken as a whole or left to Remote Desktop as a
// whole: converting only some of its files would make the others vanish from what gets pasted.
static class CopyFilter
{
    public static LeftAloneReason ReasonToLeaveAlone(List<VirtualFile> files)
    {
        List<FileTypeGroup> leftAlone = FileTypes.NotConverted();
        bool convertFolders = !leftAlone.Contains(FileTypes.Folders);
        long totalSize = 0;
        foreach (VirtualFile file in files)
        {
            // judged by the name the file gets here, which is what ends up on this machine
            string localPath = StagingFolder.ToLocalRelativePath(file.RemoteName);

            // The apps this is for cannot take folders anyway, and Remote Desktop's own handling
            // pastes them into Explorer without first downloading the whole tree.
            if (!convertFolders && (file.IsFolder || localPath.IndexOf('\\') >= 0)) return LeftAloneReason.FileType;
            if (!file.IsFolder && localPath.Length > 0 && leftAlone.Contains(FileTypes.Of(localPath))) return LeftAloneReason.FileType;
            if (file.KnownSize > 0) totalSize += file.KnownSize;
        }
        return totalSize > SizeLimit.Bytes ? LeftAloneReason.SizeLimit : LeftAloneReason.None;
    }
}

enum LeftAloneReason
{
    None,
    MeteredConnection,
    FileType,
    SizeLimit
}

// The largest copy that is still converted. The tool exists for mail attachments, and about 25 MB
// is where mail stops taking them - a larger copy would only be downloaded for nothing.
static class SizeLimit
{
    public const int MinMegabytes = 1;
    public const int MaxMegabytes = 999;
    const int DefaultMegabytes = 25;
    const long BytesPerMegabyte = 1024 * 1024;

    // a hand-edited value outside the range must not switch the limit off or block every copy
    public static int Megabytes
    {
        get { return Clamp(Settings.GetNumber(Settings.SizeLimitMB, DefaultMegabytes)); }
    }

    public static long Bytes
    {
        get { return Megabytes * BytesPerMegabyte; }
    }

    public static void Set(int megabytes)
    {
        Settings.SetNumber(Settings.SizeLimitMB, Clamp(megabytes));
    }

    static int Clamp(int megabytes)
    {
        return Math.Max(MinMegabytes, Math.Min(MaxMegabytes, megabytes));
    }
}

// One line of the file type filter: its setting, its label and the extensions it stands for.
sealed class FileTypeGroup
{
    public readonly string SettingName;
    public readonly TextId Label;
    public readonly string[] Extensions;

    readonly bool convertedByDefault;

    public FileTypeGroup(string settingName, TextId label, bool convertedByDefault, string extensions)
    {
        SettingName = settingName;
        Label = label;
        this.convertedByDefault = convertedByDefault;
        Extensions = extensions.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }

    public bool IsConverted
    {
        get { return Settings.IsOn(SettingName, convertedByDefault); }
    }

    public string Examples(int count)
    {
        if (Extensions.Length <= count) return string.Join(", ", Extensions);
        return string.Join(", ", Extensions, 0, count) + " …";
    }
}

// The file types the tray menu lets the user leave out. Each list starts with its best-known
// extensions, which are the ones the menu shows. An extension belongs to one type only.
//
// The tool exists for attaching files to mails. Applications, files without or with an unknown
// extension, and folders are rarely something one mails, so they start unticked and such a copy is
// left to Remote Desktop - which transfers nothing until it is pasted. Ticking them is one click.
static class FileTypes
{
    const bool OnByDefault = true;
    const bool OffByDefault = false;

    public static readonly FileTypeGroup Images = new FileTypeGroup("ConvertImages", TextId.FileTypeImages, OnByDefault,
        "jpg png gif bmp jpeg jpe jfif tif tiff webp heic heif avif svg ico dib psd tga emf wmf jxl raw cr2 cr3 nef arw dng orf rw2");

    public static readonly FileTypeGroup Videos = new FileTypeGroup("ConvertVideos", TextId.FileTypeVideos, OnByDefault,
        "mp4 mov avi mkv wmv webm m4v mpg mpeg flv 3gp 3g2 m2ts mts ogv vob asf f4v divx");

    public static readonly FileTypeGroup Audio = new FileTypeGroup("ConvertAudio", TextId.FileTypeAudio, OnByDefault,
        "mp3 wav flac m4a aac ogg oga opus wma aif aiff mid midi amr");

    public static readonly FileTypeGroup Pdf = new FileTypeGroup("ConvertPdf", TextId.FileTypePdf, OnByDefault,
        "pdf");

    // Microsoft Office, OpenDocument (LibreOffice, OpenOffice), mail items, and the calendar and
    // contact files that get mailed - Everything Else, where they would land otherwise, starts unticked
    public static readonly FileTypeGroup Office = new FileTypeGroup("ConvertOffice", TextId.FileTypeOffice, OnByDefault,
        "docx xlsx pptx odt ods odp doc xls ppt docm dotx dotm dot xlsm xlsb xltx xltm xlt pptm ppsx ppsm pps potx potm pot rtf " +
        "odg odf odb ott ots otp fodt fods fodp vsd vsdx vsdm pub one mpp accdb mdb msg eml oft ics vcf");

    // plain text of any kind, source code included. js is here although Windows runs it on a
    // double-click: far more of them are web sources than scripts
    public static readonly FileTypeGroup Text = new FileTypeGroup("ConvertText", TextId.FileTypeText, OnByDefault,
        "txt log csv md json xml yaml yml ini cfg conf config toml properties tsv markdown rst tex nfo srt vtt " +
        "html htm css js mjs ts tsx jsx php py rb java c h cpp hpp cs vb go rs swift kt sql sh");

    // disk images are archives of a kind, and as large
    public static readonly FileTypeGroup Archives = new FileTypeGroup("ConvertArchives", TextId.FileTypeArchives, OnByDefault,
        "zip rar 7z tar gz tgz bz2 tbz2 xz txz zst lz lz4 lzma z cab arj lzh zipx iso img vhd vhdx vmdk wim");

    // whatever Windows runs or installs on a double-click, scripts and shortcuts included
    public static readonly FileTypeGroup Applications = new FileTypeGroup("ConvertApplications", TextId.FileTypeApplications, OffByDefault,
        "exe dll msi bat cmd ps1 com scr sys cpl ocx drv msix msixbundle appx appxbundle msp msu vbs vbe wsf wsh hta jar lnk reg apk");

    public static readonly FileTypeGroup NoExtension = new FileTypeGroup("ConvertNoExtension", TextId.FileTypeNoExtension, OffByDefault, "");
    public static readonly FileTypeGroup Other = new FileTypeGroup("ConvertOtherFiles", TextId.FileTypeOther, OffByDefault, "");

    // Not a type of file, so Of never returns it, but it is ticked in the same place. The setting
    // keeps the name it had as a tray item of its own, so an earlier choice still applies.
    public static readonly FileTypeGroup Folders = new FileTypeGroup("ConvertFolders", TextId.FileTypeFolders, OffByDefault, "");

    public static readonly FileTypeGroup[] All = { Images, Videos, Audio, Pdf, Office, Text, Archives, Applications, NoExtension, Other, Folders };

    static readonly Dictionary<string, FileTypeGroup> ByExtension = IndexByExtension();

    public static FileTypeGroup Of(string path)
    {
        string name = path.Substring(path.LastIndexOf('\\') + 1);
        int dot = name.LastIndexOf('.');
        // a name like ".gitignore" is all name and no extension
        if (dot <= 0) return NoExtension;

        FileTypeGroup group;
        return ByExtension.TryGetValue(name.Substring(dot + 1), out group) ? group : Other;
    }

    public static List<FileTypeGroup> NotConverted()
    {
        List<FileTypeGroup> groups = new List<FileTypeGroup>();
        foreach (FileTypeGroup group in All)
        {
            if (!group.IsConverted) groups.Add(group);
        }
        return groups;
    }

    static Dictionary<string, FileTypeGroup> IndexByExtension()
    {
        Dictionary<string, FileTypeGroup> index = new Dictionary<string, FileTypeGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (FileTypeGroup group in All)
        {
            foreach (string extension in group.Extensions) index[extension] = group;
        }
        return index;
    }
}

static class StagingFolder
{
    public static readonly string Location = Path.Combine(Path.GetTempPath(), "RDPClip");

    // Every copy gets a folder of its own. Files from the last paste may still be open - an
    // attachment being uploaded, a document the user opened - so an old batch cannot always be
    // removed, and a fresh folder keeps its leftovers from mixing into the new copy.
    public static string StartNewBatch()
    {
        ClearPreviousBatches();
        string batchFolder = FreePathFor(Path.Combine(Location, DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(batchFolder);
        return batchFolder;
    }

    // Whatever is locked stays until a later copy.
    public static void ClearPreviousBatches()
    {
        Directory.CreateDirectory(Location);
        foreach (string file in Directory.GetFiles(Location)) TryDeleteFile(file);
        foreach (string folder in Directory.GetDirectories(Location)) TryDeleteFolder(folder);
    }

    public static void RemoveEverything()
    {
        if (!Directory.Exists(Location)) return;
        ClearPreviousBatches();
        TryDeleteFolder(Location);
    }

    public static void TryDeleteFile(string path)
    {
        if (path == null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void TryDeleteFolder(string path)
    {
        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // The names come from the remote machine and keep the path below a copied folder
    // ("folder\file.txt"). That structure is kept, but every part is cleaned on its own, so nothing
    // in a name can lead out of the batch folder: a part cannot contain a separator or a drive
    // colon, and ".." does not survive the trimming. Dots and spaces are trimmed together because
    // Windows drops both from the end of a name - ".. ." would otherwise still arrive as "..".
    public static string ToLocalRelativePath(string remoteName)
    {
        List<string> parts = new List<string>();
        foreach (string remotePart in remoteName.Split('\\', '/'))
        {
            string part = remotePart;
            foreach (char invalid in Path.GetInvalidFileNameChars()) part = part.Replace(invalid, '_');
            part = part.Trim().TrimEnd('.', ' ');
            if (part.Length > 0) parts.Add(part);
        }
        return string.Join("\\", parts.ToArray());
    }

    // The second lock on the same door: whatever a remote name turns into after cleaning, and however
    // Windows reads the result, nothing is written anywhere but below the batch folder.
    public static bool IsInside(string folder, string path)
    {
        string prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // Two remote names can become the same local one once they are cleaned.
    public static string FreePathFor(string path)
    {
        string folder = Path.GetDirectoryName(path);
        string baseName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int copy = 2; File.Exists(path) || Directory.Exists(path); copy++)
        {
            path = Path.Combine(folder, baseName + " (" + copy + ")" + extension);
        }
        return path;
    }
}

static class ClipboardRetry
{
    const int Attempts = 5;
    const int FirstDelayMilliseconds = 50;

    // mstsc holds the clipboard open for a moment whenever it changes, and anyone else opening it
    // then gets CLIPBRD_E_CANT_OPEN.
    public static T Run<T>(Func<T> clipboardAccess)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return clipboardAccess();
            }
            catch (ExternalException)
            {
                if (attempt == Attempts) throw;
                Thread.Sleep(FirstDelayMilliseconds * attempt);
            }
        }
    }
}

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

static class Installation
{
    const string ExeName = "RdpClipboardBridge.exe";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "RdpClipboardBridge";
    const int CopyAttempts = 10;
    const int CopyRetryMilliseconds = 300;

    // The per-user programs folder needs no administrator rights, which is what lets the exe
    // install itself without a setup program.
    public static readonly string InstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\RdpClipboardBridge");
    public static readonly string InstalledExePath = Path.Combine(InstallDirectory, ExeName);
    public static readonly string CurrentExePath = Application.ExecutablePath;

    public static bool IsInstalled
    {
        get { return File.Exists(InstalledExePath); }
    }

    public static bool IsRunningInstalled
    {
        get { return IsSamePath(CurrentExePath, InstalledExePath); }
    }

    // Whether the copy that is running is the installed one. That an installed exe exists does not
    // say so: it may just as well be a standalone copy that runs.
    public static bool IsInstalledCopyRunning()
    {
        bool found = false;
        try
        {
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                using (process)
                {
                    found = found || IsSamePath(TryGetExePath(process), InstalledExePath);
                }
            }
        }
        catch (Exception)
        {
        }
        return found;
    }

    // Fails for another user's process on a shared machine, which is not ours to replace anyway.
    static string TryGetExePath(Process process)
    {
        try { return process.MainModule.FileName; }
        catch (Exception) { return null; }
    }

    // A copy that was just told to exit can hold its exe a moment longer than its mutex.
    public static void CopyToInstallLocation()
    {
        Directory.CreateDirectory(InstallDirectory);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(CurrentExePath, InstalledExePath, true);
                return;
            }
            catch (IOException)
            {
                if (attempt == CopyAttempts) throw;
                Thread.Sleep(CopyRetryMilliseconds);
            }
        }
    }

    // A Run value left behind by a moved exe points elsewhere and must not count.
    public static bool IsAutostartFor(string exePath)
    {
        try
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                string command = run == null ? null : run.GetValue(RunValueName) as string;
                return command != null && IsSamePath(command.Trim().Trim('"'), exePath);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void EnableAutostart(string exePath)
    {
        using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            run.SetValue(RunValueName, "\"" + exePath + "\"", RegistryValueKind.String);
        }
    }

    public static void DisableAutostart()
    {
        using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
        {
            if (run != null) run.DeleteValue(RunValueName, false);
        }
    }

    // A running exe cannot delete itself, so a hidden cmd outlives this process and retries for a
    // few seconds until the file is free. Its working directory is %TEMP%, because a cmd sitting in
    // the install folder would keep that folder from being removed.
    public static void RemoveAfterExit()
    {
        string command = "/c for /l %i in (1,1,15) do (" +
            "ping -n 2 127.0.0.1 >nul" +
            " & del /f /q \"" + InstalledExePath + "\" 2>nul" +
            " & rmdir \"" + InstallDirectory + "\" 2>nul" +
            " & if not exist \"" + InstallDirectory + "\" exit)";
        ProcessStartInfo startInfo = new ProcessStartInfo("cmd.exe", command);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.WorkingDirectory = Path.GetTempPath();
        Process.Start(startInfo).Dispose();
    }

    static bool IsSamePath(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // not a usable path at all, e.g. a hand-edited Run value
            return false;
        }
    }
}

// What the user chose, as switches under HKCU. None of them is vital: a switch that cannot be read
// counts as not set, and one that cannot be saved is simply not saved - the tray menu shows the
// stored state the next time it opens.
static class Settings
{
    const string KeyPath = @"Software\RdpClipboardBridge";

    public const string InstallDeclined = "InstallDeclined";
    public const string PauseOnMetered = "PauseOnMetered";
    public const string SizeLimitMB = "SizeLimitMB";

    public static bool IsOn(string name)
    {
        return IsOn(name, false);
    }

    public static bool IsOn(string name, bool whenNotSet)
    {
        return GetNumber(name, whenNotSet ? 1 : 0) != 0;
    }

    public static void Set(string name, bool on)
    {
        SetNumber(name, on ? 1 : 0);
    }

    public static int GetNumber(string name, int whenNotSet)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (key == null) return whenNotSet;
                return Convert.ToInt32(key.GetValue(name, whenNotSet));
            }
        }
        catch (Exception)
        {
            return whenNotSet;
        }
    }

    public static void SetNumber(string name, int value)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key.SetValue(name, value, RegistryValueKind.DWord);
            }
        }
        catch (Exception)
        {
        }
    }

    public static void Forget()
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
    }
}

[ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
class NetworkListManager
{
}

// Only the first method is declared; it is the only one called, and the ones after it in the
// interface do not move it.
[ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface INetworkCostManager
{
    void GetCost(out uint cost, IntPtr destinationAddress);
}

// Asks Windows whether the internet connection is metered, through the Network List Manager's COM
// interface - the WinRT API for the same question needs references this build does not have.
static class NetworkCost
{
    // NLM_CONNECTION_COST: "metered connection" in the Windows settings and phone hotspots report
    // fixed, mobile data fixed or variable; roaming and an exceeded data limit are flags on top.
    const uint Fixed = 0x2;
    const uint Variable = 0x4;
    const uint OverDataLimit = 0x10000;
    const uint Roaming = 0x40000;

    // Not being able to ask counts as not metered: a failure here must not switch the tool off.
    public static bool IsMetered()
    {
        try
        {
            object manager = new NetworkListManager();
            try
            {
                uint cost;
                ((INetworkCostManager)manager).GetCost(out cost, IntPtr.Zero);
                return (cost & (Fixed | Variable | OverDataLimit | Roaming)) != 0;
            }
            finally
            {
                Marshal.ReleaseComObject(manager);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}

static class InstallFlow
{
    // Returns the installed exe to hand over to, or null to keep running from here.
    public static string Offer()
    {
        switch (InstallDialog.Ask(Installation.IsInstalled))
        {
            case InstallChoice.InstallWithAutostart:
                return Install(true);
            case InstallChoice.InstallOnly:
                return Install(false);
            case InstallChoice.RunWithoutInstalling:
                Settings.Set(Settings.InstallDeclined, true);
                return null;
            default:
                return null;
        }
    }

    // The installed copy was running and is replaced; whether it starts with Windows stays as it was.
    public static string Upgrade()
    {
        return Install(Installation.IsAutostartFor(Installation.InstalledExePath));
    }

    // The autostart entry is set either way and never just left: one that still points at the copy
    // this was installed from would keep starting that copy, which then asks to be installed at
    // every logon. Any failure leaves the user with a running standalone copy rather than nothing.
    public static string Install(bool startWithWindows)
    {
        try
        {
            Installation.CopyToInstallLocation();
            if (startWithWindows) Installation.EnableAutostart(Installation.InstalledExePath);
            else Installation.DisableAutostart();
            // only this answer: installing must not reset what was set in the tray menu
            Settings.Set(Settings.InstallDeclined, false);
            return Installation.InstalledExePath;
        }
        catch (Exception exception)
        {
            ShowError(Texts.Get(TextId.ErrorInstallFailed, Installation.InstallDirectory, exception.Message, Texts.ProductName));
            return null;
        }
    }

    public static bool ConfirmAndUninstall()
    {
        if (!AskYesNo(Texts.Get(TextId.ConfirmUninstall,
            Texts.ProductName, Installation.InstallDirectory, StagingFolder.Location))) return false;

        try
        {
            Installation.DisableAutostart();
            Settings.Forget();
            StagingFolder.RemoveEverything();
            Installation.RemoveAfterExit();
        }
        catch (Exception exception)
        {
            ShowError(Texts.Get(TextId.ErrorUninstallIncomplete, exception.Message));
        }
        return true;
    }

    // Without this, starting a newer exe while the installed one runs would quietly do nothing.
    public static bool ConfirmReplacingRunningCopy(bool installedCopyRuns)
    {
        TextId question = installedCopyRuns ? TextId.ConfirmReplaceInstalled : TextId.ConfirmReplaceRunning;
        return AskYesNo(Texts.Get(question, Texts.ProductName));
    }

    public static void ReportRunningCopyDidNotExit()
    {
        ShowError(Texts.Get(TextId.ErrorRunningCopyDidNotExit, Texts.Get(TextId.MenuExit)));
    }

    public static void Start(string exePath)
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(exePath);
            startInfo.WorkingDirectory = Path.GetDirectoryName(exePath);
            Process.Start(startInfo).Dispose();
        }
        catch (Exception exception)
        {
            ShowError(Texts.Get(TextId.ErrorStartFailed, exePath, exception.Message));
        }
    }

    static bool AskYesNo(string question)
    {
        return MessageBox.Show(question, Texts.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    static void ShowError(string message)
    {
        MessageBox.Show(message, Texts.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}

enum InstallChoice
{
    Dismissed,
    InstallWithAutostart,
    InstallOnly,
    RunWithoutInstalling
}

// A MessageBox cannot label its buttons, and these three answers need saying in words.
class InstallDialog : Form
{
    const int TextWidth = 420;
    const int Spacing = 12;

    InstallChoice choice = InstallChoice.Dismissed;

    public static InstallChoice Ask(bool replacesInstalledCopy)
    {
        using (InstallDialog dialog = new InstallDialog(replacesInstalledCopy))
        {
            dialog.ShowDialog();
            return dialog.choice;
        }
    }

    InstallDialog(bool replacesInstalledCopy)
    {
        Text = Texts.ProductName;
        Icon = AppIcon.Large;
        Font = SystemFonts.MessageBoxFont;
        // the layout is in font units, so it grows with the display scaling instead of clipping text
        AutoScaleDimensions = new SizeF(6F, 13F);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // shown before any other window of this app exists, so the taskbar is what keeps it findable
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        Button installWithAutostart = NewChoiceButton(Texts.Get(TextId.ButtonInstallWithAutostart), InstallChoice.InstallWithAutostart);

        // One column: the wrapped explanation sets the width, and the buttons stack below it at that
        // same width - side by side, the longer translations made the dialog sprawl.
        TableLayoutPanel content = new TableLayoutPanel();
        content.ColumnCount = 1;
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.AutoSize = true;
        content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.Padding = new Padding(Spacing);
        content.Controls.Add(NewExplanation(replacesInstalledCopy));
        content.Controls.Add(installWithAutostart);
        content.Controls.Add(NewChoiceButton(Texts.Get(TextId.ButtonInstallOnly), InstallChoice.InstallOnly));
        content.Controls.Add(NewChoiceButton(Texts.Get(TextId.ButtonRunWithoutInstalling), InstallChoice.RunWithoutInstalling));
        Controls.Add(content);

        AcceptButton = installWithAutostart;
        ActiveControl = installWithAutostart;
    }

    static Label NewExplanation(bool replacesInstalledCopy)
    {
        Label explanation = new Label();
        explanation.AutoSize = true;
        explanation.MaximumSize = new Size(TextWidth, 0);
        explanation.Margin = new Padding(0, 0, 0, Spacing);
        explanation.Text = Texts.Get(TextId.InstallExplanation, Texts.ProductName, Installation.InstallDirectory);
        if (replacesInstalledCopy) explanation.Text += "\n\n" + Texts.Get(TextId.InstallReplacesExisting);
        return explanation;
    }

    Button NewChoiceButton(string label, InstallChoice buttonChoice)
    {
        Button button = new Button();
        button.Text = label;
        button.AutoSize = true;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, Spacing / 3, 0, 0);
        button.Padding = new Padding(Spacing / 2, 2, Spacing / 2, 2);
        button.Click += delegate
        {
            choice = buttonChoice;
            Close();
        };
        return button;
    }

    // A newer copy that takes over asks the running one to go - and right after its start the
    // running one is nothing but this dialog.
    protected override void WndProc(ref Message message)
    {
        if (SingleInstance.IsExitRequest(message.Msg))
        {
            SingleInstance.ExitRequested = true;
            Close();
            return;
        }
        base.WndProc(ref message);
    }

    // Esc closes like the X does: nothing chosen, nothing remembered.
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }
}

// Laid out like InstallDialog: in font units, one column under a wrapped explanation.
class SizeLimitDialog : Form
{
    const int TextWidth = 320;
    const int Spacing = 12;

    readonly NumericUpDown megabytes = new NumericUpDown();

    // Returns the chosen limit in MB, or null when the dialog was cancelled or closed.
    public static int? Ask(int currentMegabytes)
    {
        using (SizeLimitDialog dialog = new SizeLimitDialog(currentMegabytes))
        {
            if (dialog.ShowDialog() != DialogResult.OK) return null;
            // reading Value takes over what was typed but not yet confirmed, within the range
            return decimal.ToInt32(dialog.megabytes.Value);
        }
    }

    SizeLimitDialog(int currentMegabytes)
    {
        Text = Texts.ProductName;
        Icon = AppIcon.Large;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleDimensions = new SizeF(6F, 13F);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // Left on, so the window stays unowned: only then does the exit request of a newer copy,
        // a broadcast, reach it.
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        megabytes.Minimum = SizeLimit.MinMegabytes;
        megabytes.Maximum = SizeLimit.MaxMegabytes;
        megabytes.DecimalPlaces = 0;
        megabytes.Value = currentMegabytes;
        megabytes.Margin = new Padding(0, 0, 0, Spacing);

        Button ok = NewButton(Texts.Get(TextId.ButtonOk), DialogResult.OK);
        Button cancel = NewButton(Texts.Get(TextId.ButtonCancel), DialogResult.Cancel);
        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.AutoSize = true;
        buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        buttons.Dock = DockStyle.Fill;
        buttons.Margin = Padding.Empty;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        TableLayoutPanel content = new TableLayoutPanel();
        content.ColumnCount = 1;
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.AutoSize = true;
        content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.Padding = new Padding(Spacing);
        content.Controls.Add(NewExplanation());
        content.Controls.Add(megabytes);
        content.Controls.Add(buttons);
        Controls.Add(content);

        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = megabytes;
    }

    static Label NewExplanation()
    {
        Label explanation = new Label();
        explanation.AutoSize = true;
        explanation.MaximumSize = new Size(TextWidth, 0);
        explanation.Margin = new Padding(0, 0, 0, Spacing);
        explanation.Text = Texts.Get(TextId.SizeLimitExplanation);
        return explanation;
    }

    static Button NewButton(string label, DialogResult result)
    {
        Button button = new Button();
        button.Text = label;
        button.AutoSize = true;
        button.DialogResult = result;
        button.Margin = new Padding(Spacing / 2, 0, 0, 0);
        button.Padding = new Padding(Spacing / 2, 2, Spacing / 2, 2);
        return button;
    }

    // a newer copy taking over closes this too, as nothing chosen
    protected override void WndProc(ref Message message)
    {
        if (SingleInstance.IsExitRequest(message.Msg))
        {
            Close();
            return;
        }
        base.WndProc(ref message);
    }
}

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

enum TextId
{
    MenuPause,
    MenuResume,
    MenuStartWithWindows,
    MenuOpenTempFolder,
    MenuPauseOnMetered,
    MenuFileTypes,
    MenuSizeLimit,
    SizeLimitExplanation,
    ButtonOk,
    ButtonCancel,
    FileTypeImages,
    FileTypeVideos,
    FileTypeAudio,
    FileTypePdf,
    FileTypeOffice,
    FileTypeText,
    FileTypeArchives,
    FileTypeApplications,
    FileTypeNoExtension,
    FileTypeOther,
    FileTypeFolders,
    MenuInstall,
    MenuUninstall,
    MenuExit,
    TrayTooltipPaused,
    TrayTooltipMetered,
    TrayTooltipFileType,
    TrayTooltipSizeLimit,
    WarningPrepareFailed,
    WarningFilesFailed,
    WarningAutostartFailed,
    WarningOpenFolderFailed,
    InstallExplanation,
    InstallReplacesExisting,
    ButtonInstallWithAutostart,
    ButtonInstallOnly,
    ButtonRunWithoutInstalling,
    ErrorInstallFailed,
    ConfirmUninstall,
    ErrorUninstallIncomplete,
    ConfirmReplaceInstalled,
    ConfirmReplaceRunning,
    ErrorRunningCopyDidNotExit,
    ErrorStartFailed
}

// Every user-visible text, per language of the Windows display language. English is the complete
// reference: a text missing in another language, and any language not listed, falls back to it.
// {0}, {1}... are filled in by the caller, so each language can put them where its grammar wants.
// The product name is passed in rather than written into the texts, so it is never translated.
static class Texts
{
    public const string ProductName = "RDP Clipboard Bridge";

    // keyed by two-letter ISO 639-1 code; adding a language is adding one block here
    static readonly Dictionary<string, Dictionary<TextId, string>> Languages = new Dictionary<string, Dictionary<TextId, string>>
    {
        { "en", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "Pause" },
            { TextId.MenuResume, "Resume" },
            { TextId.MenuStartWithWindows, "Start with Windows" },
            { TextId.MenuOpenTempFolder, "Open Temp Folder" },
            { TextId.MenuPauseOnMetered, "Pause on Metered Connections" },
            { TextId.MenuFileTypes, "File Types" },
            { TextId.MenuSizeLimit, "Size Limit: {0} MB…" },
            { TextId.SizeLimitExplanation, "Largest copy that is still converted, in MB (1 to 999). Anything larger is left to Remote Desktop." },
            { TextId.ButtonOk, "OK" },
            { TextId.ButtonCancel, "Cancel" },
            { TextId.FileTypeImages, "Images" },
            { TextId.FileTypeVideos, "Videos" },
            { TextId.FileTypeAudio, "Audio" },
            { TextId.FileTypePdf, "PDF Documents" },
            { TextId.FileTypeOffice, "Office Documents" },
            { TextId.FileTypeText, "Text Files" },
            { TextId.FileTypeArchives, "Archives" },
            { TextId.FileTypeApplications, "Applications" },
            { TextId.FileTypeNoExtension, "No Extension" },
            { TextId.FileTypeOther, "Everything Else" },
            { TextId.FileTypeFolders, "Folders" },
            { TextId.MenuInstall, "Install…" },
            { TextId.MenuUninstall, "Uninstall" },
            { TextId.MenuExit, "Exit" },
            { TextId.TrayTooltipPaused, "{0} (paused)" },
            { TextId.TrayTooltipMetered, "{0} (paused: metered connection)" },
            { TextId.TrayTooltipFileType, "{0} (not converted: file type)" },
            { TextId.TrayTooltipSizeLimit, "{0} (not converted: over {1} MB)" },
            { TextId.WarningPrepareFailed, "Could not prepare the copied files: {0}" },
            { TextId.WarningFilesFailed, "Files that could not be copied from the remote session: {0}" },
            { TextId.WarningAutostartFailed, "Could not change the autostart setting: {0}" },
            { TextId.WarningOpenFolderFailed, "Could not open {0}: {1}" },
            { TextId.InstallExplanation, "{0} makes files copied in a Remote Desktop session pasteable in the new Outlook and similar apps. Install it for this user into {1}? No administrator rights are needed." },
            { TextId.InstallReplacesExisting, "The copy already installed there is replaced." },
            { TextId.ButtonInstallWithAutostart, "Install and start with Windows" },
            { TextId.ButtonInstallOnly, "Install only" },
            { TextId.ButtonRunWithoutInstalling, "Run without installing" },
            { TextId.ErrorInstallFailed, "Could not install to {0}:\n{1}\n\n{2} keeps running from where it is." },
            { TextId.ConfirmUninstall, "Uninstall {0}?\n\nThis removes it from {1}, stops it starting with Windows and deletes the copied files in {2}." },
            { TextId.ErrorUninstallIncomplete, "Uninstalling did not finish:\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} is already installed and running.\n\nReplace the installed copy with this version? It is closed, updated and started again; whether it starts with Windows stays as it is." },
            { TextId.ConfirmReplaceRunning, "{0} is already running.\n\nClose the running copy and continue with this one?" },
            { TextId.ErrorRunningCopyDidNotExit, "The running copy did not close. Choose \"{0}\" from its tray menu, then start this one again." },
            { TextId.ErrorStartFailed, "Could not start {0}:\n{1}" }
        } },
        { "de", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "Pausieren" },
            { TextId.MenuResume, "Fortsetzen" },
            { TextId.MenuStartWithWindows, "Mit Windows starten" },
            { TextId.MenuOpenTempFolder, "Temp-Ordner öffnen" },
            { TextId.MenuPauseOnMetered, "Bei getakteter Verbindung pausieren" },
            { TextId.MenuFileTypes, "Dateitypen" },
            { TextId.MenuSizeLimit, "Größenlimit: {0} MB…" },
            { TextId.SizeLimitExplanation, "Größte Kopie, die noch umgewandelt wird, in MB (1 bis 999). Größere Kopien bleiben dem Remotedesktop überlassen." },
            { TextId.ButtonOk, "OK" },
            { TextId.ButtonCancel, "Abbrechen" },
            { TextId.FileTypeImages, "Bilder" },
            { TextId.FileTypeVideos, "Videos" },
            { TextId.FileTypeAudio, "Audio" },
            { TextId.FileTypePdf, "PDF-Dokumente" },
            { TextId.FileTypeOffice, "Office-Dokumente" },
            { TextId.FileTypeText, "Textdateien" },
            { TextId.FileTypeArchives, "Archive" },
            { TextId.FileTypeApplications, "Programme" },
            { TextId.FileTypeNoExtension, "Ohne Dateiendung" },
            { TextId.FileTypeOther, "Alles andere" },
            { TextId.FileTypeFolders, "Ordner" },
            { TextId.MenuInstall, "Installieren…" },
            { TextId.MenuUninstall, "Deinstallieren" },
            { TextId.MenuExit, "Beenden" },
            { TextId.TrayTooltipPaused, "{0} (pausiert)" },
            { TextId.TrayTooltipMetered, "{0} (pausiert: getaktete Verbindung)" },
            { TextId.TrayTooltipFileType, "{0} (nicht umgewandelt: Dateityp)" },
            { TextId.TrayTooltipSizeLimit, "{0} (nicht umgewandelt: über {1} MB)" },
            { TextId.WarningPrepareFailed, "Die kopierten Dateien konnten nicht vorbereitet werden: {0}" },
            { TextId.WarningFilesFailed, "Dateien, die nicht aus der Remotesitzung kopiert werden konnten: {0}" },
            { TextId.WarningAutostartFailed, "Die Autostart-Einstellung konnte nicht geändert werden: {0}" },
            { TextId.WarningOpenFolderFailed, "{0} konnte nicht geöffnet werden: {1}" },
            { TextId.InstallExplanation, "Mit {0} lassen sich Dateien, die in einer Remotedesktopsitzung kopiert wurden, im neuen Outlook und ähnlichen Apps einfügen. Möchten Sie es für diesen Benutzer in {1} installieren? Administratorrechte sind nicht erforderlich." },
            { TextId.InstallReplacesExisting, "Die dort bereits installierte Kopie wird ersetzt." },
            { TextId.ButtonInstallWithAutostart, "Installieren und mit Windows starten" },
            { TextId.ButtonInstallOnly, "Nur installieren" },
            { TextId.ButtonRunWithoutInstalling, "Ohne Installation ausführen" },
            { TextId.ErrorInstallFailed, "Die Installation in {0} ist fehlgeschlagen:\n{1}\n\n{2} läuft weiter vom bisherigen Speicherort aus." },
            { TextId.ConfirmUninstall, "{0} deinstallieren?\n\nDabei wird es aus {1} entfernt und nicht mehr mit Windows gestartet, und die kopierten Dateien in {2} werden gelöscht." },
            { TextId.ErrorUninstallIncomplete, "Die Deinstallation wurde nicht vollständig abgeschlossen:\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} ist bereits installiert und wird ausgeführt.\n\nMöchten Sie die installierte Kopie durch diese Version ersetzen? Sie wird beendet, aktualisiert und neu gestartet; die Autostart-Einstellung bleibt unverändert." },
            { TextId.ConfirmReplaceRunning, "{0} wird bereits ausgeführt.\n\nMöchten Sie die laufende Kopie beenden und mit dieser fortfahren?" },
            { TextId.ErrorRunningCopyDidNotExit, "Die laufende Kopie wurde nicht beendet. Wählen Sie in ihrem Menü im Infobereich „{0}“ und starten Sie diese Datei dann erneut." },
            { TextId.ErrorStartFailed, "{0} konnte nicht gestartet werden:\n{1}" }
        } },
        { "fr", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "Suspendre" },
            { TextId.MenuResume, "Reprendre" },
            { TextId.MenuStartWithWindows, "Démarrer avec Windows" },
            { TextId.MenuOpenTempFolder, "Ouvrir le dossier temporaire" },
            { TextId.MenuPauseOnMetered, "Suspendre sur connexion limitée" },
            { TextId.MenuFileTypes, "Types de fichiers" },
            { TextId.MenuSizeLimit, "Limite de taille : {0} Mo…" },
            { TextId.SizeLimitExplanation, "Plus grande copie encore convertie, en Mo (1 à 999). Toute copie plus grande est laissée au Bureau à distance." },
            { TextId.ButtonOk, "OK" },
            { TextId.ButtonCancel, "Annuler" },
            { TextId.FileTypeImages, "Images" },
            { TextId.FileTypeVideos, "Vidéos" },
            { TextId.FileTypeAudio, "Audio" },
            { TextId.FileTypePdf, "Documents PDF" },
            { TextId.FileTypeOffice, "Documents Office" },
            { TextId.FileTypeText, "Fichiers texte" },
            { TextId.FileTypeArchives, "Archives" },
            { TextId.FileTypeApplications, "Applications" },
            { TextId.FileTypeNoExtension, "Sans extension" },
            { TextId.FileTypeOther, "Tout le reste" },
            { TextId.FileTypeFolders, "Dossiers" },
            { TextId.MenuInstall, "Installer…" },
            { TextId.MenuUninstall, "Désinstaller" },
            { TextId.MenuExit, "Quitter" },
            { TextId.TrayTooltipPaused, "{0} (suspendu)" },
            { TextId.TrayTooltipMetered, "{0} (suspendu : connexion limitée)" },
            { TextId.TrayTooltipFileType, "{0} (non converti : type de fichier)" },
            { TextId.TrayTooltipSizeLimit, "{0} (non converti : plus de {1} Mo)" },
            { TextId.WarningPrepareFailed, "Impossible de préparer les fichiers copiés : {0}" },
            { TextId.WarningFilesFailed, "Fichiers qui n’ont pas pu être copiés depuis la session à distance : {0}" },
            { TextId.WarningAutostartFailed, "Impossible de modifier le démarrage automatique : {0}" },
            { TextId.WarningOpenFolderFailed, "Impossible d’ouvrir {0} : {1}" },
            { TextId.InstallExplanation, "{0} permet de coller dans le nouvel Outlook et les applications similaires les fichiers copiés dans une session Bureau à distance. L’installer pour cet utilisateur dans {1} ? Aucun droit d’administrateur n’est nécessaire." },
            { TextId.InstallReplacesExisting, "La copie déjà installée à cet emplacement sera remplacée." },
            { TextId.ButtonInstallWithAutostart, "Installer et démarrer avec Windows" },
            { TextId.ButtonInstallOnly, "Installer uniquement" },
            { TextId.ButtonRunWithoutInstalling, "Exécuter sans installer" },
            { TextId.ErrorInstallFailed, "Impossible d’installer dans {0} :\n{1}\n\n{2} continue de s’exécuter depuis son emplacement actuel." },
            { TextId.ConfirmUninstall, "Désinstaller {0} ?\n\nIl sera supprimé de {1}, ne démarrera plus avec Windows et les fichiers copiés dans {2} seront effacés." },
            { TextId.ErrorUninstallIncomplete, "La désinstallation n’a pas abouti :\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} est déjà installé et en cours d’exécution.\n\nRemplacer la copie installée par cette version ? Elle sera fermée, mise à jour puis redémarrée ; le démarrage avec Windows reste inchangé." },
            { TextId.ConfirmReplaceRunning, "{0} est déjà en cours d’exécution.\n\nFermer la copie en cours d’exécution et continuer avec celle-ci ?" },
            { TextId.ErrorRunningCopyDidNotExit, "La copie en cours d’exécution ne s’est pas fermée. Choisissez « {0} » dans son menu de la zone de notification, puis relancez celle-ci." },
            { TextId.ErrorStartFailed, "Impossible de démarrer {0} :\n{1}" }
        } },
        { "it", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "Sospendi" },
            { TextId.MenuResume, "Riprendi" },
            { TextId.MenuStartWithWindows, "Avvia con Windows" },
            { TextId.MenuOpenTempFolder, "Apri cartella temporanea" },
            { TextId.MenuPauseOnMetered, "Sospendi con connessione a consumo" },
            { TextId.MenuFileTypes, "Tipi di file" },
            { TextId.MenuSizeLimit, "Limite dimensioni: {0} MB…" },
            { TextId.SizeLimitExplanation, "Copia più grande che viene ancora convertita, in MB (da 1 a 999). Le copie più grandi vengono lasciate a Desktop remoto." },
            { TextId.ButtonOk, "OK" },
            { TextId.ButtonCancel, "Annulla" },
            { TextId.FileTypeImages, "Immagini" },
            { TextId.FileTypeVideos, "Video" },
            { TextId.FileTypeAudio, "Audio" },
            { TextId.FileTypePdf, "Documenti PDF" },
            { TextId.FileTypeOffice, "Documenti Office" },
            { TextId.FileTypeText, "File di testo" },
            { TextId.FileTypeArchives, "Archivi" },
            { TextId.FileTypeApplications, "Applicazioni" },
            { TextId.FileTypeNoExtension, "Senza estensione" },
            { TextId.FileTypeOther, "Tutto il resto" },
            { TextId.FileTypeFolders, "Cartelle" },
            { TextId.MenuInstall, "Installa…" },
            { TextId.MenuUninstall, "Disinstalla" },
            { TextId.MenuExit, "Esci" },
            { TextId.TrayTooltipPaused, "{0} (in pausa)" },
            { TextId.TrayTooltipMetered, "{0} (in pausa: connessione a consumo)" },
            { TextId.TrayTooltipFileType, "{0} (non convertito: tipo di file)" },
            { TextId.TrayTooltipSizeLimit, "{0} (non convertito: oltre {1} MB)" },
            { TextId.WarningPrepareFailed, "Impossibile preparare i file copiati: {0}" },
            { TextId.WarningFilesFailed, "File che non è stato possibile copiare dalla sessione remota: {0}" },
            { TextId.WarningAutostartFailed, "Impossibile modificare l'avvio automatico: {0}" },
            { TextId.WarningOpenFolderFailed, "Impossibile aprire {0}: {1}" },
            { TextId.InstallExplanation, "{0} consente di incollare nel nuovo Outlook e in app simili i file copiati in una sessione Desktop remoto. Installarlo per questo utente in {1}? Non sono necessari diritti di amministratore." },
            { TextId.InstallReplacesExisting, "La copia già installata in quella posizione verrà sostituita." },
            { TextId.ButtonInstallWithAutostart, "Installa e avvia con Windows" },
            { TextId.ButtonInstallOnly, "Installa soltanto" },
            { TextId.ButtonRunWithoutInstalling, "Esegui senza installare" },
            { TextId.ErrorInstallFailed, "Impossibile installare in {0}:\n{1}\n\n{2} continua a funzionare dalla posizione attuale." },
            { TextId.ConfirmUninstall, "Disinstallare {0}?\n\nVerrà rimosso da {1}, non verrà più avviato con Windows e i file copiati in {2} verranno eliminati." },
            { TextId.ErrorUninstallIncomplete, "La disinstallazione non è stata completata:\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} è già installato e in esecuzione.\n\nSostituire la copia installata con questa versione? Verrà chiusa, aggiornata e riavviata; l'impostazione di avvio con Windows rimane invariata." },
            { TextId.ConfirmReplaceRunning, "{0} è già in esecuzione.\n\nChiudere la copia in esecuzione e continuare con questa?" },
            { TextId.ErrorRunningCopyDidNotExit, "La copia in esecuzione non si è chiusa. Scegliere «{0}» dal suo menu nell'area di notifica, quindi avviare di nuovo questa copia." },
            { TextId.ErrorStartFailed, "Impossibile avviare {0}:\n{1}" }
        } },
        { "es", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "Pausar" },
            { TextId.MenuResume, "Reanudar" },
            { TextId.MenuStartWithWindows, "Iniciar con Windows" },
            { TextId.MenuOpenTempFolder, "Abrir carpeta temporal" },
            { TextId.MenuPauseOnMetered, "Pausar con conexión de uso medido" },
            { TextId.MenuFileTypes, "Tipos de archivo" },
            { TextId.MenuSizeLimit, "Límite de tamaño: {0} MB…" },
            { TextId.SizeLimitExplanation, "Copia más grande que aún se convierte, en MB (de 1 a 999). Las copias más grandes se dejan a Escritorio remoto." },
            { TextId.ButtonOk, "Aceptar" },
            { TextId.ButtonCancel, "Cancelar" },
            { TextId.FileTypeImages, "Imágenes" },
            { TextId.FileTypeVideos, "Vídeos" },
            { TextId.FileTypeAudio, "Audio" },
            { TextId.FileTypePdf, "Documentos PDF" },
            { TextId.FileTypeOffice, "Documentos de Office" },
            { TextId.FileTypeText, "Archivos de texto" },
            { TextId.FileTypeArchives, "Archivos comprimidos" },
            { TextId.FileTypeApplications, "Aplicaciones" },
            { TextId.FileTypeNoExtension, "Sin extensión" },
            { TextId.FileTypeOther, "Todo lo demás" },
            { TextId.FileTypeFolders, "Carpetas" },
            { TextId.MenuInstall, "Instalar…" },
            { TextId.MenuUninstall, "Desinstalar" },
            { TextId.MenuExit, "Salir" },
            { TextId.TrayTooltipPaused, "{0} (en pausa)" },
            { TextId.TrayTooltipMetered, "{0} (en pausa: conexión de uso medido)" },
            { TextId.TrayTooltipFileType, "{0} (sin convertir: tipo de archivo)" },
            { TextId.TrayTooltipSizeLimit, "{0} (sin convertir: más de {1} MB)" },
            { TextId.WarningPrepareFailed, "No se pudieron preparar los archivos copiados: {0}" },
            { TextId.WarningFilesFailed, "Archivos que no se pudieron copiar desde la sesión remota: {0}" },
            { TextId.WarningAutostartFailed, "No se pudo cambiar el inicio automático: {0}" },
            { TextId.WarningOpenFolderFailed, "No se pudo abrir {0}: {1}" },
            { TextId.InstallExplanation, "{0} permite pegar en el nuevo Outlook y en aplicaciones similares los archivos copiados en una sesión de Escritorio remoto. ¿Instalarlo para este usuario en {1}? No se necesitan derechos de administrador." },
            { TextId.InstallReplacesExisting, "Se reemplazará la copia ya instalada allí." },
            { TextId.ButtonInstallWithAutostart, "Instalar e iniciar con Windows" },
            { TextId.ButtonInstallOnly, "Solo instalar" },
            { TextId.ButtonRunWithoutInstalling, "Ejecutar sin instalar" },
            { TextId.ErrorInstallFailed, "No se pudo instalar en {0}:\n{1}\n\n{2} sigue ejecutándose desde su ubicación actual." },
            { TextId.ConfirmUninstall, "¿Desinstalar {0}?\n\nSe quitará de {1}, dejará de iniciarse con Windows y se eliminarán los archivos copiados en {2}." },
            { TextId.ErrorUninstallIncomplete, "La desinstalación no se completó:\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} ya está instalado y en ejecución.\n\n¿Reemplazar la copia instalada por esta versión? Se cerrará, se actualizará y se volverá a iniciar; el inicio con Windows se mantiene como está." },
            { TextId.ConfirmReplaceRunning, "{0} ya se está ejecutando.\n\n¿Cerrar la copia en ejecución y continuar con esta?" },
            { TextId.ErrorRunningCopyDidNotExit, "La copia en ejecución no se cerró. Elija «{0}» en su menú del área de notificación y vuelva a iniciar esta." },
            { TextId.ErrorStartFailed, "No se pudo iniciar {0}:\n{1}" }
        } },
        { "pt", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "Pausar" },
            { TextId.MenuResume, "Retomar" },
            { TextId.MenuStartWithWindows, "Iniciar com o Windows" },
            { TextId.MenuOpenTempFolder, "Abrir pasta temporária" },
            { TextId.MenuPauseOnMetered, "Pausar em conexão limitada" },
            { TextId.MenuFileTypes, "Tipos de arquivo" },
            { TextId.MenuSizeLimit, "Limite de tamanho: {0} MB…" },
            { TextId.SizeLimitExplanation, "Maior cópia que ainda é convertida, em MB (de 1 a 999). Cópias maiores ficam a cargo da Área de Trabalho Remota." },
            { TextId.ButtonOk, "OK" },
            { TextId.ButtonCancel, "Cancelar" },
            { TextId.FileTypeImages, "Imagens" },
            { TextId.FileTypeVideos, "Vídeos" },
            { TextId.FileTypeAudio, "Áudio" },
            { TextId.FileTypePdf, "Documentos PDF" },
            { TextId.FileTypeOffice, "Documentos do Office" },
            { TextId.FileTypeText, "Arquivos de texto" },
            { TextId.FileTypeArchives, "Arquivos compactados" },
            { TextId.FileTypeApplications, "Aplicativos" },
            { TextId.FileTypeNoExtension, "Sem extensão" },
            { TextId.FileTypeOther, "Todo o resto" },
            { TextId.FileTypeFolders, "Pastas" },
            { TextId.MenuInstall, "Instalar…" },
            { TextId.MenuUninstall, "Desinstalar" },
            { TextId.MenuExit, "Sair" },
            { TextId.TrayTooltipPaused, "{0} (pausado)" },
            { TextId.TrayTooltipMetered, "{0} (pausado: conexão limitada)" },
            { TextId.TrayTooltipFileType, "{0} (não convertido: tipo de arquivo)" },
            { TextId.TrayTooltipSizeLimit, "{0} (não convertido: acima de {1} MB)" },
            { TextId.WarningPrepareFailed, "Não foi possível preparar os arquivos copiados: {0}" },
            { TextId.WarningFilesFailed, "Arquivos que não puderam ser copiados da sessão remota: {0}" },
            { TextId.WarningAutostartFailed, "Não foi possível alterar a inicialização automática: {0}" },
            { TextId.WarningOpenFolderFailed, "Não foi possível abrir {0}: {1}" },
            { TextId.InstallExplanation, "{0} permite colar no novo Outlook e em aplicativos semelhantes os arquivos copiados em uma sessão de Área de Trabalho Remota. Instalar para este usuário em {1}? Não são necessários direitos de administrador." },
            { TextId.InstallReplacesExisting, "A cópia já instalada nesse local será substituída." },
            { TextId.ButtonInstallWithAutostart, "Instalar e iniciar com o Windows" },
            { TextId.ButtonInstallOnly, "Somente instalar" },
            { TextId.ButtonRunWithoutInstalling, "Executar sem instalar" },
            { TextId.ErrorInstallFailed, "Não foi possível instalar em {0}:\n{1}\n\n{2} continua em execução a partir do local atual." },
            { TextId.ConfirmUninstall, "Desinstalar {0}?\n\nEle será removido de {1}, deixará de iniciar com o Windows e os arquivos copiados em {2} serão excluídos." },
            { TextId.ErrorUninstallIncomplete, "A desinstalação não foi concluída:\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} já está instalado e em execução.\n\nSubstituir a cópia instalada por esta versão? Ela será fechada, atualizada e iniciada novamente; a inicialização com o Windows permanece como está." },
            { TextId.ConfirmReplaceRunning, "{0} já está em execução.\n\nFechar a cópia em execução e continuar com esta?" },
            { TextId.ErrorRunningCopyDidNotExit, "A cópia em execução não foi fechada. Escolha \"{0}\" no menu dela na área de notificação e inicie esta novamente." },
            { TextId.ErrorStartFailed, "Não foi possível iniciar {0}:\n{1}" }
        } },
        { "ru", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "Приостановить" },
            { TextId.MenuResume, "Возобновить" },
            { TextId.MenuStartWithWindows, "Запускать вместе с Windows" },
            { TextId.MenuOpenTempFolder, "Открыть временную папку" },
            { TextId.MenuPauseOnMetered, "Приостанавливать при лимитном подключении" },
            { TextId.MenuFileTypes, "Типы файлов" },
            { TextId.MenuSizeLimit, "Ограничение размера: {0} МБ…" },
            { TextId.SizeLimitExplanation, "Наибольший размер копии, которая ещё преобразуется, в МБ (от 1 до 999). Более крупные копии остаются за удалённым рабочим столом." },
            { TextId.ButtonOk, "ОК" },
            { TextId.ButtonCancel, "Отмена" },
            { TextId.FileTypeImages, "Изображения" },
            { TextId.FileTypeVideos, "Видео" },
            { TextId.FileTypeAudio, "Аудио" },
            { TextId.FileTypePdf, "Документы PDF" },
            { TextId.FileTypeOffice, "Документы Office" },
            { TextId.FileTypeText, "Текстовые файлы" },
            { TextId.FileTypeArchives, "Архивы" },
            { TextId.FileTypeApplications, "Приложения" },
            { TextId.FileTypeNoExtension, "Без расширения" },
            { TextId.FileTypeOther, "Всё остальное" },
            { TextId.FileTypeFolders, "Папки" },
            { TextId.MenuInstall, "Установить…" },
            { TextId.MenuUninstall, "Удалить" },
            { TextId.MenuExit, "Выход" },
            { TextId.TrayTooltipPaused, "{0} (приостановлено)" },
            { TextId.TrayTooltipMetered, "{0} (приостановлено: лимитное подключение)" },
            { TextId.TrayTooltipFileType, "{0} (не преобразовано: тип файла)" },
            { TextId.TrayTooltipSizeLimit, "{0} (не преобразовано: больше {1} МБ)" },
            { TextId.WarningPrepareFailed, "Не удалось подготовить скопированные файлы: {0}" },
            { TextId.WarningFilesFailed, "Число файлов, которые не удалось скопировать из удалённого сеанса: {0}" },
            { TextId.WarningAutostartFailed, "Не удалось изменить параметр автозапуска: {0}" },
            { TextId.WarningOpenFolderFailed, "Не удалось открыть {0}: {1}" },
            { TextId.InstallExplanation, "{0} позволяет вставлять файлы, скопированные в сеансе удалённого рабочего стола, в новый Outlook и похожие приложения. Установить для этого пользователя в {1}? Права администратора не требуются." },
            { TextId.InstallReplacesExisting, "Уже установленная там копия будет заменена." },
            { TextId.ButtonInstallWithAutostart, "Установить и запускать вместе с Windows" },
            { TextId.ButtonInstallOnly, "Только установить" },
            { TextId.ButtonRunWithoutInstalling, "Запустить без установки" },
            { TextId.ErrorInstallFailed, "Не удалось установить в {0}:\n{1}\n\n{2} продолжит работу из текущего расположения." },
            { TextId.ConfirmUninstall, "Удалить {0}?\n\nПрограмма будет удалена из {1} и перестанет запускаться вместе с Windows, а скопированные файлы в {2} будут удалены." },
            { TextId.ErrorUninstallIncomplete, "Удаление не завершено:\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} уже установлен и запущен.\n\nЗаменить установленную копию этой версией? Она будет закрыта, обновлена и запущена снова; параметр автозапуска не изменится." },
            { TextId.ConfirmReplaceRunning, "{0} уже запущен.\n\nЗакрыть запущенную копию и продолжить с этой?" },
            { TextId.ErrorRunningCopyDidNotExit, "Запущенная копия не закрылась. Выберите «{0}» в её меню в области уведомлений, затем запустите эту копию снова." },
            { TextId.ErrorStartFailed, "Не удалось запустить {0}:\n{1}" }
        } },
        { "zh", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "暂停" },
            { TextId.MenuResume, "继续" },
            { TextId.MenuStartWithWindows, "随 Windows 启动" },
            { TextId.MenuOpenTempFolder, "打开临时文件夹" },
            { TextId.MenuPauseOnMetered, "使用按流量计费的连接时暂停" },
            { TextId.MenuFileTypes, "文件类型" },
            { TextId.MenuSizeLimit, "大小限制：{0} MB…" },
            { TextId.SizeLimitExplanation, "仍会转换的最大复制大小（MB，1 到 999）。更大的复制内容将交由远程桌面处理。" },
            { TextId.ButtonOk, "确定" },
            { TextId.ButtonCancel, "取消" },
            { TextId.FileTypeImages, "图片" },
            { TextId.FileTypeVideos, "视频" },
            { TextId.FileTypeAudio, "音频" },
            { TextId.FileTypePdf, "PDF 文档" },
            { TextId.FileTypeOffice, "Office 文档" },
            { TextId.FileTypeText, "文本文件" },
            { TextId.FileTypeArchives, "压缩文件" },
            { TextId.FileTypeApplications, "应用程序" },
            { TextId.FileTypeNoExtension, "无扩展名" },
            { TextId.FileTypeOther, "其他所有文件" },
            { TextId.FileTypeFolders, "文件夹" },
            { TextId.MenuInstall, "安装…" },
            { TextId.MenuUninstall, "卸载" },
            { TextId.MenuExit, "退出" },
            { TextId.TrayTooltipPaused, "{0}（已暂停）" },
            { TextId.TrayTooltipMetered, "{0}（已暂停：按流量计费的连接）" },
            { TextId.TrayTooltipFileType, "{0}（未转换：文件类型）" },
            { TextId.TrayTooltipSizeLimit, "{0}（未转换：超过 {1} MB）" },
            { TextId.WarningPrepareFailed, "无法准备复制的文件：{0}" },
            { TextId.WarningFilesFailed, "无法从远程会话复制的文件数：{0}" },
            { TextId.WarningAutostartFailed, "无法更改自动启动设置：{0}" },
            { TextId.WarningOpenFolderFailed, "无法打开 {0}：{1}" },
            { TextId.InstallExplanation, "{0} 可将在远程桌面会话中复制的文件粘贴到新版 Outlook 和类似应用中。是否为当前用户安装到 {1}？无需管理员权限。" },
            { TextId.InstallReplacesExisting, "该位置已安装的副本将被替换。" },
            { TextId.ButtonInstallWithAutostart, "安装并随 Windows 启动" },
            { TextId.ButtonInstallOnly, "仅安装" },
            { TextId.ButtonRunWithoutInstalling, "不安装，直接运行" },
            { TextId.ErrorInstallFailed, "无法安装到 {0}：\n{1}\n\n{2} 将继续从当前位置运行。" },
            { TextId.ConfirmUninstall, "要卸载 {0} 吗？\n\n这会将其从 {1} 中删除，不再随 Windows 启动，并删除 {2} 中复制的文件。" },
            { TextId.ErrorUninstallIncomplete, "卸载未完成：\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} 已安装并正在运行。\n\n要用此版本替换已安装的副本吗？它将被关闭、更新并重新启动；是否随 Windows 启动的设置保持不变。" },
            { TextId.ConfirmReplaceRunning, "{0} 已在运行。\n\n要关闭正在运行的副本并继续使用此副本吗？" },
            { TextId.ErrorRunningCopyDidNotExit, "正在运行的副本未关闭。请在其通知区域菜单中选择“{0}”，然后重新启动此副本。" },
            { TextId.ErrorStartFailed, "无法启动 {0}：\n{1}" }
        } },
        { "ja", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "一時停止" },
            { TextId.MenuResume, "再開" },
            { TextId.MenuStartWithWindows, "Windows の起動時に開始" },
            { TextId.MenuOpenTempFolder, "一時フォルダーを開く" },
            { TextId.MenuPauseOnMetered, "従量制課金接続では一時停止" },
            { TextId.MenuFileTypes, "ファイルの種類" },
            { TextId.MenuSizeLimit, "サイズの上限: {0} MB…" },
            { TextId.SizeLimitExplanation, "変換するコピーの最大サイズ (MB、1 ～ 999)。これより大きいコピーはリモート デスクトップに任せます。" },
            { TextId.ButtonOk, "OK" },
            { TextId.ButtonCancel, "キャンセル" },
            { TextId.FileTypeImages, "画像" },
            { TextId.FileTypeVideos, "ビデオ" },
            { TextId.FileTypeAudio, "オーディオ" },
            { TextId.FileTypePdf, "PDF ドキュメント" },
            { TextId.FileTypeOffice, "Office ドキュメント" },
            { TextId.FileTypeText, "テキスト ファイル" },
            { TextId.FileTypeArchives, "圧縮ファイル" },
            { TextId.FileTypeApplications, "アプリケーション" },
            { TextId.FileTypeNoExtension, "拡張子なし" },
            { TextId.FileTypeOther, "その他すべて" },
            { TextId.FileTypeFolders, "フォルダー" },
            { TextId.MenuInstall, "インストール…" },
            { TextId.MenuUninstall, "アンインストール" },
            { TextId.MenuExit, "終了" },
            { TextId.TrayTooltipPaused, "{0} (一時停止中)" },
            { TextId.TrayTooltipMetered, "{0} (一時停止中: 従量制課金接続)" },
            { TextId.TrayTooltipFileType, "{0} (未変換: ファイルの種類)" },
            { TextId.TrayTooltipSizeLimit, "{0} (未変換: {1} MB 超)" },
            { TextId.WarningPrepareFailed, "コピーしたファイルを準備できませんでした: {0}" },
            { TextId.WarningFilesFailed, "リモート セッションからコピーできなかったファイル数: {0}" },
            { TextId.WarningAutostartFailed, "自動起動の設定を変更できませんでした: {0}" },
            { TextId.WarningOpenFolderFailed, "{0} を開けませんでした: {1}" },
            { TextId.InstallExplanation, "{0} を使うと、リモート デスクトップ セッションでコピーしたファイルを新しい Outlook などのアプリに貼り付けられるようになります。このユーザー用に {1} にインストールしますか? 管理者権限は必要ありません。" },
            { TextId.InstallReplacesExisting, "そこに既にインストールされているコピーは置き換えられます。" },
            { TextId.ButtonInstallWithAutostart, "インストールして Windows の起動時に開始" },
            { TextId.ButtonInstallOnly, "インストールのみ" },
            { TextId.ButtonRunWithoutInstalling, "インストールせずに実行" },
            { TextId.ErrorInstallFailed, "{0} にインストールできませんでした:\n{1}\n\n{2} は現在の場所から引き続き実行されます。" },
            { TextId.ConfirmUninstall, "{0} をアンインストールしますか?\n\n{1} から削除され、Windows の起動時に開始されなくなり、{2} 内のコピーしたファイルが削除されます。" },
            { TextId.ErrorUninstallIncomplete, "アンインストールが完了しませんでした:\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} は既にインストールされ、実行中です。\n\nインストール済みのコピーをこのバージョンに置き換えますか? いったん終了し、更新してから再起動します。Windows の起動時に開始するかどうかの設定は変わりません。" },
            { TextId.ConfirmReplaceRunning, "{0} は既に実行中です。\n\n実行中のコピーを終了して、このコピーで続行しますか?" },
            { TextId.ErrorRunningCopyDidNotExit, "実行中のコピーが終了しませんでした。通知領域のメニューから [{0}] を選んでから、このコピーをもう一度起動してください。" },
            { TextId.ErrorStartFailed, "{0} を開始できませんでした:\n{1}" }
        } },
        { "id", new Dictionary<TextId, string>
        {
            { TextId.MenuPause, "Jeda" },
            { TextId.MenuResume, "Lanjutkan" },
            { TextId.MenuStartWithWindows, "Mulai bersama Windows" },
            { TextId.MenuOpenTempFolder, "Buka Folder Sementara" },
            { TextId.MenuPauseOnMetered, "Jeda pada Koneksi Terukur" },
            { TextId.MenuFileTypes, "Jenis File" },
            { TextId.MenuSizeLimit, "Batas Ukuran: {0} MB…" },
            { TextId.SizeLimitExplanation, "Salinan terbesar yang masih dikonversi, dalam MB (1 hingga 999). Salinan yang lebih besar diserahkan ke Desktop Jarak Jauh." },
            { TextId.ButtonOk, "OK" },
            { TextId.ButtonCancel, "Batal" },
            { TextId.FileTypeImages, "Gambar" },
            { TextId.FileTypeVideos, "Video" },
            { TextId.FileTypeAudio, "Audio" },
            { TextId.FileTypePdf, "Dokumen PDF" },
            { TextId.FileTypeOffice, "Dokumen Office" },
            { TextId.FileTypeText, "File Teks" },
            { TextId.FileTypeArchives, "Arsip" },
            { TextId.FileTypeApplications, "Aplikasi" },
            { TextId.FileTypeNoExtension, "Tanpa Ekstensi" },
            { TextId.FileTypeOther, "Lainnya" },
            { TextId.FileTypeFolders, "Folder" },
            { TextId.MenuInstall, "Instal…" },
            { TextId.MenuUninstall, "Hapus Instalan" },
            { TextId.MenuExit, "Keluar" },
            { TextId.TrayTooltipPaused, "{0} (dijeda)" },
            { TextId.TrayTooltipMetered, "{0} (dijeda: koneksi terukur)" },
            { TextId.TrayTooltipFileType, "{0} (tidak dikonversi: jenis file)" },
            { TextId.TrayTooltipSizeLimit, "{0} (tidak dikonversi: lebih dari {1} MB)" },
            { TextId.WarningPrepareFailed, "Tidak dapat menyiapkan file yang disalin: {0}" },
            { TextId.WarningFilesFailed, "File yang tidak dapat disalin dari sesi jarak jauh: {0}" },
            { TextId.WarningAutostartFailed, "Tidak dapat mengubah pengaturan mulai otomatis: {0}" },
            { TextId.WarningOpenFolderFailed, "Tidak dapat membuka {0}: {1}" },
            { TextId.InstallExplanation, "{0} membuat file yang disalin dalam sesi Desktop Jarak Jauh dapat ditempelkan di Outlook baru dan aplikasi serupa. Instal untuk pengguna ini di {1}? Tidak memerlukan hak administrator." },
            { TextId.InstallReplacesExisting, "Salinan yang sudah terinstal di sana akan diganti." },
            { TextId.ButtonInstallWithAutostart, "Instal dan mulai bersama Windows" },
            { TextId.ButtonInstallOnly, "Instal saja" },
            { TextId.ButtonRunWithoutInstalling, "Jalankan tanpa menginstal" },
            { TextId.ErrorInstallFailed, "Tidak dapat menginstal ke {0}:\n{1}\n\n{2} tetap berjalan dari lokasinya saat ini." },
            { TextId.ConfirmUninstall, "Hapus instalan {0}?\n\nTindakan ini menghapusnya dari {1}, menghentikannya mulai bersama Windows, dan menghapus file yang disalin di {2}." },
            { TextId.ErrorUninstallIncomplete, "Penghapusan instalan tidak selesai:\n{0}" },
            { TextId.ConfirmReplaceInstalled, "{0} sudah terinstal dan sedang berjalan.\n\nGanti salinan yang terinstal dengan versi ini? Salinan itu akan ditutup, diperbarui, dan dimulai lagi; pengaturan mulai bersama Windows tetap seperti sebelumnya." },
            { TextId.ConfirmReplaceRunning, "{0} sudah berjalan.\n\nTutup salinan yang sedang berjalan dan lanjutkan dengan yang ini?" },
            { TextId.ErrorRunningCopyDidNotExit, "Salinan yang sedang berjalan tidak tertutup. Pilih \"{0}\" dari menu area pemberitahuannya, lalu mulai salinan ini lagi." },
            { TextId.ErrorStartFailed, "Tidak dapat memulai {0}:\n{1}" }
        } }
    };

    static readonly Dictionary<TextId, string> English = Languages["en"];
    static readonly CultureInfo DisplayLanguage = NeutralDisplayLanguage();
    static readonly Dictionary<TextId, string> Current = TranslationFor(DisplayLanguage);

    public static string Get(TextId id, params object[] values)
    {
        string text;
        if (!Current.TryGetValue(id, out text)) text = English[id];
        return values.Length == 0 ? text : string.Format(text, values);
    }

    // de-CH and de-DE both end up at de, pt-BR at pt, zh-CN and zh-TW at zh.
    static CultureInfo NeutralDisplayLanguage()
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        while (!culture.IsNeutralCulture && !culture.Equals(CultureInfo.InvariantCulture)) culture = culture.Parent;
        return culture;
    }

    static Dictionary<TextId, string> TranslationFor(CultureInfo language)
    {
        Dictionary<TextId, string> texts;
        return Languages.TryGetValue(language.TwoLetterISOLanguageName, out texts) ? texts : English;
    }
}
