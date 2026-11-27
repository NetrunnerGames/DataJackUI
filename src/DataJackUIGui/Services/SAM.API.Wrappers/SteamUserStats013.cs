using System.Runtime.InteropServices;
using SAM.API.Interfaces;

namespace SAM.API.Wrappers;

public class SteamUserStats013 : NativeWrapper<ISteamUserStats013>
{
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetStatInt(nint self, nint name, out int data);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetStatFloat(nint self, nint name, out float data);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeSetStatInt(nint self, nint name, int data);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeSetStatFloat(nint self, nint name, float data);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetAchievement(nint self, nint name, [MarshalAs(UnmanagedType.I1)] out bool isAchieved);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeSetAchievement(nint self, nint name);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeClearAchievement(nint self, nint name);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetAchievementAndUnlockTime(nint self, nint name, [MarshalAs(UnmanagedType.I1)] out bool isAchieved, out uint unlockTime);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeStoreStats(nint self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeGetAchievementIcon(nint self, nint name);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint NativeGetAchievementDisplayAttribute(nint self, nint name, nint key);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate CallHandle NativeRequestUserStats(nint self, ulong steamIdUser);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeResetAllStats(nint self, [MarshalAs(UnmanagedType.I1)] bool achievementsToo);

    public bool GetStatValue(string name, out int value)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(name);
        return GetFunction<NativeGetStatInt>(Functions.GetStatInteger)(ObjectAddress, stringHandle.Handle, out value);
    }

    public bool GetStatValue(string name, out float value)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(name);
        return GetFunction<NativeGetStatFloat>(Functions.GetStatFloat)(ObjectAddress, stringHandle.Handle, out value);
    }

    public bool SetStatValue(string name, int value)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(name);
        return Call<bool, NativeSetStatInt>(Functions.SetStatInteger, new object[3] { ObjectAddress, stringHandle.Handle, value });
    }

    public bool SetStatValue(string name, float value)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(name);
        return Call<bool, NativeSetStatFloat>(Functions.SetStatFloat, new object[3] { ObjectAddress, stringHandle.Handle, value });
    }

    public bool GetAchievement(string name, out bool isAchieved)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(name);
        return GetFunction<NativeGetAchievement>(Functions.GetAchievement)(ObjectAddress, stringHandle.Handle, out isAchieved);
    }

    public bool SetAchievement(string name, bool state)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(name);
        if (!state)
        {
            return Call<bool, NativeClearAchievement>(Functions.ClearAchievement, new object[2] { ObjectAddress, stringHandle.Handle });
        }
        return Call<bool, NativeSetAchievement>(Functions.SetAchievement, new object[2] { ObjectAddress, stringHandle.Handle });
    }

    public bool GetAchievementAndUnlockTime(string name, out bool isAchieved, out uint unlockTime)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(name);
        return GetFunction<NativeGetAchievementAndUnlockTime>(Functions.GetAchievementAndUnlockTime)(ObjectAddress, stringHandle.Handle, out isAchieved, out unlockTime);
    }

    public bool StoreStats()
    {
        return Call<bool, NativeStoreStats>(Functions.StoreStats, new object[1] { ObjectAddress });
    }

    public int GetAchievementIcon(string name)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(name);
        return Call<int, NativeGetAchievementIcon>(Functions.GetAchievementIcon, new object[2] { ObjectAddress, stringHandle.Handle });
    }

    public string? GetAchievementDisplayAttribute(string name, string key)
    {
        using NativeStrings.StringHandle stringHandle = NativeStrings.StringToStringHandle(name);
        using NativeStrings.StringHandle stringHandle2 = NativeStrings.StringToStringHandle(key);
        return NativeStrings.PointerToString(Call<nint, NativeGetAchievementDisplayAttribute>(Functions.GetAchievementDisplayAttribute, new object[3] { ObjectAddress, stringHandle.Handle, stringHandle2.Handle }));
    }

    public CallHandle RequestUserStats(ulong steamIdUser)
    {
        return Call<CallHandle, NativeRequestUserStats>(Functions.RequestUserStats, new object[2] { ObjectAddress, steamIdUser });
    }

    public bool ResetAllStats(bool achievementsToo)
    {
        return Call<bool, NativeResetAllStats>(Functions.ResetAllStats, new object[2] { ObjectAddress, achievementsToo });
    }
}
