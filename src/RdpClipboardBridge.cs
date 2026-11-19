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
    // How long after the press that completes the count a clipboard update is still converted on
    // demand: the announcement of the copy may still be on its way from the session.
    const int OnDemandRequestMilliseconds = 2000;
    const int OnDemandSettleMilliseconds = 400;
    // long enough to be seen when there was little or nothing to download
    const int BusyIconMinimumMilliseconds = 800;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RemoveClipboardFormatListener(IntPtr window);

    readonly NotifyIcon trayIcon = new NotifyIcon();
    readonly ForegroundWatcher foregroundWatcher;
    readonly ToolStripMenuItem pauseMenuItem;
    readonly ToolStripMenuItem startWithWindowsMenuItem;
    readonly ToolStripMenuItem automaticMenuItem;
    readonly ToolStripMenuItem convertAutomaticallyMenuItem;
    readonly ToolStripMenuItem fileTypesMenuItem;
    readonly ToolStripMenuItem sizeLimitMenuItem;
    readonly ToolStripMenuItem pauseOnMeteredMenuItem;
    readonly ToolStripMenuItem onDemandMenuItem;
    // only there while On Demand is on
    CopyKeyWatcher copyKeyWatcher;
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
    // a click and a transfer may overlap, so the busy icon is counted rather than switched
    readonly System.Windows.Forms.Timer busyIconTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer onDemandSettleTimer = new System.Windows.Forms.Timer();
    int busyCount;
    int busySinceTick;
    // a click's pass may end in the close it was asked for, after which the tray icon is gone
    bool closed;
    // when the last update arrived, which a pass busy with the clipboard may only look at later
    int lastClipboardUpdateTick;
    // On Demand or a click on the tray icon: a request arrived that no pass has looked at yet
    bool onDemandLookPending;
    // On Demand: a request is open, from the press at onDemandPressTick on
    bool onDemandRequested;
    int onDemandPressTick;
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
        convertAutomaticallyMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuConvertAutomatically), null, OnConvertAutomaticallyClicked);
        fileTypesMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuFileTypes));
        foreach (FileTypeGroup fileType in FileTypes.All) fileTypesMenuItem.DropDownItems.Add(NewFileTypeMenuItem(fileType));
        sizeLimitMenuItem = new ToolStripMenuItem(SizeLimitMenuText(), null, OnSizeLimitClicked);
        pauseOnMeteredMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuPauseOnMetered), null, OnPauseOnMeteredClicked);
        automaticMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuAutomatic));
        automaticMenuItem.DropDownItems.Add(convertAutomaticallyMenuItem);
        automaticMenuItem.DropDownItems.Add(new ToolStripSeparator());
        automaticMenuItem.DropDownItems.Add(fileTypesMenuItem);
        automaticMenuItem.DropDownItems.Add(sizeLimitMenuItem);
        automaticMenuItem.DropDownItems.Add(pauseOnMeteredMenuItem);
        onDemandMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuOnDemand));
        onDemandMenuItem.DropDownItems.Add(NewOnDemandMenuItem(OnDemand.Off, Texts.Get(TextId.MenuOnDemandOff)));
        foreach (int copies in OnDemand.Choices) onDemandMenuItem.DropDownItems.Add(NewOnDemandMenuItem(copies, Texts.Get(TextId.MenuOnDemandCopies, copies)));
        ToolStripMenuItem installMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuInstall), null, OnInstallClicked);
        ToolStripMenuItem uninstallMenuItem = new ToolStripMenuItem(Texts.Get(TextId.MenuUninstall), null, OnUninstallClicked);
        installMenuItem.Visible = !Installation.IsRunningInstalled;
        uninstallMenuItem.Visible = Installation.IsRunningInstalled;

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add(pauseMenuItem);
        menu.Items.Add(startWithWindowsMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(automaticMenuItem);
        menu.Items.Add(onDemandMenuItem);
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
        trayIcon.MouseClick += OnTrayIconClicked;
        busyIconTimer.Tick += OnBusyIconTimeUp;
        onDemandSettleTimer.Interval = OnDemandSettleMilliseconds;
        onDemandSettleTimer.Tick += OnOnDemandSettled;
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

    ToolStripMenuItem NewOnDemandMenuItem(int copies, string text)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text, null, OnOnDemandClicked);
        item.Tag = copies;
        return item;
    }

    // A click closes the whole menu, which would mean opening it again for every file type to tick.
    // The click is reported before the menu is told to close, so that is where it is decided - for
    // all three levels, because the close runs through the submenu, Automatic and the menu above.
    // A click anywhere else decides to close, or a file type ticked before would keep the menu open
    // after it. Untested on a real desktop: should cancelling not hold, the menu simply closes after
    // each click, as a plain menu would.
    void KeepOpenWhileTickingFileTypes(ContextMenuStrip menu)
    {
        menu.ItemClicked += delegate { keepMenuOpen = false; };
        automaticMenuItem.DropDown.ItemClicked += delegate { keepMenuOpen = false; };
        onDemandMenuItem.DropDown.ItemClicked += delegate { keepMenuOpen = false; };
        fileTypesMenuItem.DropDown.ItemClicked += delegate { keepMenuOpen = true; };
        menu.Closing += OnMenuClosing;
        automaticMenuItem.DropDown.Closing += OnMenuClosing;
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
        // not before the handle exists, which the request is handed on through
        UpdateCopyKeyWatcher();
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
        closed = true;
        foregroundWatcher.Dispose();
        RemoveCopyKeyWatcher();
        busyIconTimer.Dispose();
        onDemandSettleTimer.Dispose();
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
            lastClipboardUpdateTick = Environment.TickCount;
            Bridge();
        }
        base.WndProc(ref message);
    }

    void OnForegroundChanged()
    {
        foregroundChanged = true;
        Bridge();
    }

    // The keyboard hook is installed only while On Demand is on: with it off, the exe watches no keys
    // at all. See CopyKeyWatcher for why.
    void UpdateCopyKeyWatcher()
    {
        RemoveCopyKeyWatcher();
        int copies = OnDemand.Copies;
        if (copies != OnDemand.Off) copyKeyWatcher = new CopyKeyWatcher(copies, OnCopyKeysPressedOnHookThread);
    }

    void RemoveCopyKeyWatcher()
    {
        ForgetOnDemandRequest();
        if (copyKeyWatcher == null) return;
        copyKeyWatcher.Dispose();
        copyKeyWatcher = null;
    }

    // Hands the request over to the UI thread without waiting for it: the hook thread has to answer
    // the keyboard at once.
    void OnCopyKeysPressedOnHookThread(int pressTick)
    {
        try
        {
            BeginInvoke(new Action<int>(OnCopyKeysPressed), pressTick);
        }
        catch (Exception)
        {
            // the window is gone - closing, and the request with it
        }
    }

    // The announcement of the copy may be on the clipboard already, from the first press, or still on
    // its way from the session. An update that arrives within OnDemandRequestMilliseconds of the
    // press is converted on demand; without one, the clipboard is looked at as it is - but only
    // after a moment. The last press makes the session copy once more, and that takes away the files
    // a transfer started in the same instant is still reading: every such transfer failed.
    void OnCopyKeysPressed(int pressTick)
    {
        // a request still on its way when On Demand was switched off, or one made while paused
        if (copyKeyWatcher == null || paused) return;

        onDemandRequested = true;
        onDemandPressTick = pressTick;
        onDemandSettleTimer.Stop();
        onDemandSettleTimer.Start();
    }

    void OnOnDemandSettled(object sender, EventArgs e)
    {
        onDemandSettleTimer.Stop();
        if (!onDemandRequested || paused) return;
        onDemandLookPending = true;
        Bridge();
    }

    // A left click converts what is on the clipboard now, like On Demand but without its window for
    // a copy still on its way: the copy was made before the click. It works whatever Automatic and
    // On Demand are set to; only Pause stops it. The busy icon shows that the click was taken, also
    // when the copy was converted already or there is nothing to convert.
    void OnTrayIconClicked(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (paused) return;

        onDemandLookPending = true;
        BeginBusyIcon();
        try
        {
            Bridge();
        }
        finally
        {
            EndBusyIcon();
        }
    }

    void BeginBusyIcon()
    {
        if (busyCount++ > 0) return;
        busySinceTick = Environment.TickCount;
        busyIconTimer.Stop();
        trayIcon.Icon = AppIcon.SmallBusy;
    }

    void EndBusyIcon()
    {
        if (--busyCount > 0 || closed) return;
        int shownFor = unchecked(Environment.TickCount - busySinceTick);
        if (shownFor >= BusyIconMinimumMilliseconds)
        {
            trayIcon.Icon = AppIcon.Small;
            return;
        }
        busyIconTimer.Interval = BusyIconMinimumMilliseconds - shownFor;
        busyIconTimer.Start();
    }

    void OnBusyIconTimeUp(object sender, EventArgs e)
    {
        busyIconTimer.Stop();
        // a transfer may have started meanwhile
        if (busyCount == 0) trayIcon.Icon = AppIcon.Small;
    }

    void ForgetOnDemandRequest()
    {
        onDemandRequested = false;
        onDemandLookPending = false;
        onDemandSettleTimer.Stop();
    }

    bool IsAskedForOnDemand(bool lookRequested, string copiedFiles)
    {
        if (lookRequested) return true;
        if (!onDemandRequested) return false;
        // unchecked, so that the difference stays right when TickCount wraps around
        int updateAfterPress = unchecked(lastClipboardUpdateTick - onDemandPressTick);
        return updateAfterPress >= 0 && updateAfterPress <= OnDemandRequestMilliseconds;
    }

    // Every question put to the clipboard is a COM call into mstsc, and COM keeps dispatching window
    // messages while it waits - so the next clipboard update or change of window arrives in the
    // middle of this, and so does an On Demand request. Those are only noted, and the pass that is
    // running goes round again for them.
    void Bridge()
    {
        if (bridging) return;

        bridging = true;
        try
        {
            while (!paused && !closeWhenIdle && (clipboardUpdates != updatesLookedAt || onDemandLookPending || foregroundChanged))
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
            if (clipboardUpdates != updatesLookedAt || onDemandLookPending)
            {
                bool lookRequested = onDemandLookPending;
                updatesLookedAt = clipboardUpdates;
                onDemandLookPending = false;
                StageClipboard(lookRequested);
            }
            foregroundChanged = false;
            HandOverStagedCopy();
        }
        catch (Exception exception)
        {
            ShowWarning(Texts.Get(TextId.WarningPrepareFailed, exception.Message));
        }
    }

    // Downloads what was copied in the session, without touching the clipboard yet. lookRequested is
    // true when an On Demand request or a click on the tray icon, not a clipboard update, sent the
    // pass here - it converts whatever the filter says.
    void StageClipboard(bool lookRequested)
    {
        bool virtualFilesOnly = VirtualFileClipboard.HasVirtualFilesOnly();
        bool fromRemoteDesktop = RemoteDesktop.OwnsClipboard();
        if (!virtualFilesOnly || !fromRemoteDesktop)
        {
            LeaveAlone(LeftAloneReason.None);
            return;
        }

        using (ClipboardFiles clipboard = VirtualFileClipboard.ReadClipboardFiles())
        {
            bool onDemand = IsAskedForOnDemand(lookRequested, clipboard.CopiedFiles);
            // The same copy, announced once more or asked for on demand after Automatic took it: it
            // is downloaded already, and must not be judged again either - a copy converted on
            // demand would be dropped by the filter.
            // Unless files of it failed: then copying it again is the way to try once more.
            if (staged != null && staged.CopiedFiles == clipboard.CopiedFiles && staged.FailedCount == 0) return;

            LeftAloneReason reason = onDemand ? LeftAloneReason.None : CopyFilter.ReasonToLeaveAlone(clipboard.Files);
            if (reason != LeftAloneReason.None)
            {
                LeaveAlone(reason);
                return;
            }

            // a copy left alone before is converted on demand now, so the tooltip's reason is past
            ShowLeftAlone(LeftAloneReason.None);
            StagedCopy copy;
            BeginBusyIcon();
            try
            {
                copy = VirtualFileClipboard.Stage(clipboard, IsAbandoned);
            }
            finally
            {
                EndBusyIcon();
            }
            staged = copy.IsWorthKeeping ? copy : null;
        }
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
        ShowAutomaticSettings();
        foreach (ToolStripMenuItem item in fileTypesMenuItem.DropDownItems)
        {
            item.Checked = ((FileTypeGroup)item.Tag).IsConverted;
        }
        int onDemandCopies = OnDemand.Copies;
        foreach (ToolStripMenuItem item in onDemandMenuItem.DropDownItems)
        {
            item.Checked = (int)item.Tag == onDemandCopies;
        }
    }

    // The filters only apply to what is converted automatically, so they are greyed out without it.
    void ShowAutomaticSettings()
    {
        bool automatic = Settings.IsOn(Settings.AutoConvert, true);
        convertAutomaticallyMenuItem.Checked = automatic;
        fileTypesMenuItem.Enabled = automatic;
        sizeLimitMenuItem.Enabled = automatic;
        pauseOnMeteredMenuItem.Enabled = automatic;
        pauseOnMeteredMenuItem.Checked = Settings.IsOn(Settings.PauseOnMetered);
        sizeLimitMenuItem.Text = SizeLimitMenuText();
    }

    static string SizeLimitMenuText()
    {
        return Texts.Get(TextId.MenuSizeLimit, SizeLimit.Megabytes);
    }

    // The switches below apply from the next copy; what is on the clipboard now stays as it is.
    void OnConvertAutomaticallyClicked(object sender, EventArgs e)
    {
        Settings.Set(Settings.AutoConvert, !convertAutomaticallyMenuItem.Checked);
        // whatever reason the tooltip names, the copy was judged by switches that changed meaning
        ShowLeftAlone(LeftAloneReason.None);
    }

    void OnOnDemandClicked(object sender, EventArgs e)
    {
        OnDemand.Set((int)((ToolStripMenuItem)sender).Tag);
        UpdateCopyKeyWatcher();
    }

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
        ForgetOnDemandRequest();
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
