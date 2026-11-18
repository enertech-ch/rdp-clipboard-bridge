using System;
using System.Runtime.InteropServices;

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
