using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

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

    // A copy that was just told to exit can hold its exe a moment longer than its mutex: replacing
    // it then fails with a sharing violation or, for the delete, with access denied.
    public static void CopyToInstallLocation()
    {
        Directory.CreateDirectory(InstallDirectory);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                WriteFreshCopy(CurrentExePath, InstalledExePath);
                return;
            }
            catch (Exception exception)
            {
                if (!(exception is IOException || exception is UnauthorizedAccessException)) throw;
                if (attempt == CopyAttempts) throw;
                Thread.Sleep(CopyRetryMilliseconds);
            }
        }
    }

    // A downloaded exe carries the browser's "from the internet" mark, the alternate data stream
    // Zone.Identifier. The exe is not signed, so a marked installed copy would bring Windows' security
    // warning back at every logon, although the user already accepted it for the file they started.
    // Not File.Copy: it copies alternate streams along. And not deleting the stream afterwards:
    // removing that mark is an attacker technique that endpoint security products flag. Only the
    // content is copied, like a setup program writes its files. Overwriting in place would keep the
    // streams of an earlier install, so the copy goes to a new file that then takes the old one's
    // place - and a failed write leaves the old copy as it was.
    static void WriteFreshCopy(string sourcePath, string targetPath)
    {
        string newPath = targetPath + ".new";
        try
        {
            File.Delete(newPath);
            using (FileStream source = File.OpenRead(sourcePath))
            using (FileStream target = new FileStream(newPath, FileMode.CreateNew, FileAccess.Write))
            {
                source.CopyTo(target);
            }
            File.Delete(targetPath);
            File.Move(newPath, targetPath);
        }
        catch (Exception)
        {
            // a file left behind would keep uninstalling from removing the folder
            try { File.Delete(newPath); }
            catch (Exception) { }
            throw;
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
