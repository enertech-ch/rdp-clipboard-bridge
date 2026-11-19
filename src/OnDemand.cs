using System;

// The On Demand mode: pressing Ctrl+C several times in a row in a Remote Desktop window converts
// that copy whatever the Automatic mode would do with it - switched off, file types, size limit,
// metered connection. Only Pause still stops it. The presses are counted by CopyKeyWatcher.
static class OnDemand
{
    public const int Off = 0;
    public static readonly int[] Choices = { 2, 3, 4 };

    // a hand-edited value that is not one of the choices counts as off rather than as some count
    public static int Copies
    {
        get
        {
            int copies = Settings.GetNumber(Settings.OnDemandCopies, Off);
            return Array.IndexOf(Choices, copies) >= 0 ? copies : Off;
        }
    }

    public static void Set(int copies)
    {
        Settings.SetNumber(Settings.OnDemandCopies, Array.IndexOf(Choices, copies) >= 0 ? copies : Off);
    }
}
