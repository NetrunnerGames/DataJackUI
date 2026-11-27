using System.Runtime.InteropServices;
using SAM.API.Interfaces;

namespace SAM.API.Wrappers;

public class SteamUser012 : NativeWrapper<ISteamUser012>
{
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeLoggedOn(nint self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NativeGetSteamId(nint self, out ulong steamId);

    public bool IsLoggedIn()
    {
        return Call<bool, NativeLoggedOn>(Functions.LoggedOn, new object[1] { ObjectAddress });
    }

    public ulong GetSteamId()
    {
        GetFunction<NativeGetSteamId>(Functions.GetSteamID)(ObjectAddress, out var steamId);
        return steamId;
    }
}
