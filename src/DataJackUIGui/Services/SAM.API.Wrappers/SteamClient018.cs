using System.Runtime.InteropServices;
using SAM.API.Interfaces;
using SAM.API.Types;

namespace SAM.API.Wrappers;

public class SteamClient018 : NativeWrapper<ISteamClient018>
{
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeCreateSteamPipe(nint self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeReleaseSteamPipe(nint self, int pipe);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeCreateLocalUser(nint self, ref int pipe, AccountType type);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeConnectToGlobalUser(nint self, int pipe);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NativeReleaseUser(nint self, int pipe, int user);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NativeSetLocalIPBinding(nint self, uint host, ushort port);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint NativeGetISteamUser(nint self, int user, int pipe, nint version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint NativeGetISteamUserStats(nint self, int user, int pipe, nint version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint NativeGetISteamUtils(nint self, int pipe, nint version);

    private delegate nint NativeGetISteamApps(int user, int pipe, nint version);

    public int CreateSteamPipe()
    {
        return Call<int, NativeCreateSteamPipe>(Functions.CreateSteamPipe, new object[1] { ObjectAddress });
    }

    public bool ReleaseSteamPipe(int pipe)
    {
        return Call<bool, NativeReleaseSteamPipe>(Functions.ReleaseSteamPipe, new object[2] { ObjectAddress, pipe });
    }

    public int CreateLocalUser(ref int pipe, AccountType type)
    {
        return GetFunction<NativeCreateLocalUser>(Functions.CreateLocalUser)(ObjectAddress, ref pipe, type);
    }

    public int ConnectToGlobalUser(int pipe)
    {
        return Call<int, NativeConnectToGlobalUser>(Functions.ConnectToGlobalUser, new object[2] { ObjectAddress, pipe });
    }

    public void ReleaseUser(int pipe, int user)
    {
        Call<NativeReleaseUser>(Functions.ReleaseUser, new object[3] { ObjectAddress, pipe, user });
    }

    public void SetLocalIPBinding(uint host, ushort port)
    {
        Call<NativeSetLocalIPBinding>(Functions.SetLocalIPBinding, new object[3] { ObjectAddress, host, port });
    }

    private TClass GetISteamUser<TClass>(int user, int pipe, string version) where TClass : INativeWrapper, new()
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(version);
        nint objectAddress = Call<nint, NativeGetISteamUser>(Functions.GetISteamUser, new object[4] { ObjectAddress, user, pipe, stringHandle.Handle });
        TClass result = new TClass();
        result.SetupFunctions(objectAddress);
        return result;
    }

    public SteamUser012 GetSteamUser012(int user, int pipe)
    {
        return GetISteamUser<SteamUser012>(user, pipe, "SteamUser012");
    }

    private TClass GetISteamUserStats<TClass>(int user, int pipe, string version) where TClass : INativeWrapper, new()
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(version);
        nint objectAddress = Call<nint, NativeGetISteamUserStats>(Functions.GetISteamUserStats, new object[4] { ObjectAddress, user, pipe, stringHandle.Handle });
        TClass result = new TClass();
        result.SetupFunctions(objectAddress);
        return result;
    }

    public SteamUserStats013 GetSteamUserStats013(int user, int pipe)
    {
        return GetISteamUserStats<SteamUserStats013>(user, pipe, "STEAMUSERSTATS_INTERFACE_VERSION013");
    }

    public TClass GetISteamUtils<TClass>(int pipe, string version) where TClass : INativeWrapper, new()
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(version);
        nint objectAddress = Call<nint, NativeGetISteamUtils>(Functions.GetISteamUtils, new object[3] { ObjectAddress, pipe, stringHandle.Handle });
        TClass result = new TClass();
        result.SetupFunctions(objectAddress);
        return result;
    }

    public SteamUtils005 GetSteamUtils004(int pipe)
    {
        return GetISteamUtils<SteamUtils005>(pipe, "SteamUtils005");
    }

    private TClass GetISteamApps<TClass>(int user, int pipe, string version) where TClass : INativeWrapper, new()
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(version);
        nint objectAddress = Call<nint, NativeGetISteamApps>(Functions.GetISteamApps, new object[3] { user, pipe, stringHandle.Handle });
        TClass result = new TClass();
        result.SetupFunctions(objectAddress);
        return result;
    }

    public SteamApps001 GetSteamApps001(int user, int pipe)
    {
        return GetISteamApps<SteamApps001>(user, pipe, "STEAMAPPS_INTERFACE_VERSION001");
    }

    public SteamApps008 GetSteamApps008(int user, int pipe)
    {
        return GetISteamApps<SteamApps008>(user, pipe, "STEAMAPPS_INTERFACE_VERSION008");
    }
}
