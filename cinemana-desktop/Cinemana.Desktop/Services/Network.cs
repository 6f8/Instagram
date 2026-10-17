using System.Runtime.InteropServices;

namespace Cinemana.Desktop.Services;

/// <summary>Whether Windows marks the current connection as metered (limited data plan or hotspot).</summary>
public static class Network
{
    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destination);
    }
    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager;
    private const uint Unrestricted = 0x1;

    public static bool IsMetered()
    {
        try
        {
            var manager = (INetworkCostManager)new NetworkListManager();
            manager.GetCost(out uint cost, IntPtr.Zero);
            Marshal.ReleaseComObject(manager);
            return cost != 0 && (cost & Unrestricted) == 0;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException) { return false; }
    }
}
