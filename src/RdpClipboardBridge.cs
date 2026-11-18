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
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

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
        menu.Items.Add(Texts.Get(TextId.MenuAbout), null, OnAboutClicked);
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

    void OnAboutClicked(object sender, EventArgs e)
    {
        WhileDialogOpen(AboutDialog.Open);
    }

    void OnExitClicked(object sender, EventArgs e)
    {
        Close();
    }

    void WhileDialogOpen(Action dialog)
    {
        WhileDialogOpen<bool>(delegate
        {
            dialog();
            return true;
        });
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
