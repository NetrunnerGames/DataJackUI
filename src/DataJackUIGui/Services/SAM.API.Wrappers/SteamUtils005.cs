using System;
using System.Runtime.InteropServices;
using SAM.API.Interfaces;

namespace SAM.API.Wrappers;

public class SteamUtils005 : NativeWrapper<ISteamUtils005>
{
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeGetConnectedUniverse(nint self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint NativeGetIPCountry(nint self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetImageSize(nint self, int index, out int width, out int height);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetImageRGBA(nint self, int index, byte[] buffer, int length);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint NativeGetAppId(nint self);

    public int GetConnectedUniverse()
    {
        return Call<int, NativeGetConnectedUniverse>(Functions.GetConnectedUniverse, new object[1] { ObjectAddress });
    }

    public string? GetIPCountry()
    {
        return NativeStrings.PointerToString(Call<nint, NativeGetIPCountry>(Functions.GetIPCountry, new object[1] { ObjectAddress }));
    }

    public bool GetImageSize(int index, out int width, out int height)
    {
        return GetFunction<NativeGetImageSize>(Functions.GetImageSize)(ObjectAddress, index, out width, out height);
    }

    public bool GetImageRGBA(int index, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return GetFunction<NativeGetImageRGBA>(Functions.GetImageRGBA)(ObjectAddress, index, data, data.Length);
    }

    public uint GetAppId()
    {
        return Call<uint, NativeGetAppId>(Functions.GetAppID, new object[1] { ObjectAddress });
    }
}
