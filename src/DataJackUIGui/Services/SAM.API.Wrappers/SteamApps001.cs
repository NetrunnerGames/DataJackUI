using System.Runtime.InteropServices;
using SAM.API.Interfaces;

namespace SAM.API.Wrappers;

public class SteamApps001 : NativeWrapper<ISteamApps001>
{
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeGetAppData(nint self, uint appId, nint key, nint value, int valueLength);

    public string? GetAppData(uint appId, string key)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(key);
        nint num = Marshal.AllocHGlobal(1024);
        string? result = ((Call<int, NativeGetAppData>(Functions.GetAppData, new object[5] { ObjectAddress, appId, stringHandle.Handle, num, 1024 }) == 0) ? null : NativeStrings.PointerToString(num, 1024));
        Marshal.FreeHGlobal(num);
        return result;
    }
}
