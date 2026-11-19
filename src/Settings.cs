using System;
using Microsoft.Win32;

// What the user chose, as switches under HKCU. None of them is vital: a switch that cannot be read
// counts as not set, and one that cannot be saved is simply not saved - the tray menu shows the
// stored state the next time it opens.
static class Settings
{
    const string KeyPath = @"Software\RdpClipboardBridge";

    public const string InstallDeclined = "InstallDeclined";
    public const string PauseOnMetered = "PauseOnMetered";
    public const string SizeLimitMB = "SizeLimitMB";
    public const string AutoConvert = "AutoConvert";
    public const string OnDemandCopies = "OnDemandCopies";

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
