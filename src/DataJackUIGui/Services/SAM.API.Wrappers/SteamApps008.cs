using System.Runtime.InteropServices;
using SAM.API.Interfaces;

namespace SAM.API.Wrappers;

public class SteamApps008 : NativeWrapper<ISteamApps008>
{
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeIsSubscribedApp(nint self, uint gameId);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint NativeGetCurrentGameLanguage(nint self);

    public bool IsSubscribedApp(uint gameId)
    {
        return Call<bool, NativeIsSubscribedApp>(Functions.IsSubscribedApp, new object[2] { ObjectAddress, gameId });
    }

    public string? GetCurrentGameLanguage()
    {
        return NativeStrings.PointerToString(Call<nint, NativeGetCurrentGameLanguage>(Functions.GetCurrentGameLanguage, new object[1] { ObjectAddress }));
    }
}
