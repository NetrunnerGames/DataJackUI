using System.Runtime.InteropServices;

namespace SAM.API.Interfaces;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ISteamApps001
{
    public nint GetAppData;
}
