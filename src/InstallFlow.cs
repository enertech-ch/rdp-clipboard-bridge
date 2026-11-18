using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

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
            // Not through the shell, which is where Windows checks for the download mark. The
            // installed copy is written without one; this keeps the hand-over quiet even if it had.
            ProcessStartInfo startInfo = new ProcessStartInfo(exePath);
            startInfo.UseShellExecute = false;
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
