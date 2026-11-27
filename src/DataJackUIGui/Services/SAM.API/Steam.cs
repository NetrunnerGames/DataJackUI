using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SAM.API.Types;

namespace SAM.API;

public static class Steam
{
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    private struct Native
    {
        internal const uint LoadWithAlteredSearchPath = 8u;

        [DllImport("kernel32.dll", BestFitMapping = false, SetLastError = true, ThrowOnUnmappableChar = true)]
        internal static extern nint GetProcAddress(nint module, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint LoadLibraryEx(string path, nint file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetDllDirectory(string path);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate nint NativeCreateInterface(string version, nint returnCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeSteamGetCallback(int pipe, out CallbackMessage message, out int call);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeSteamFreeLastCallback(int pipe);

    private static nint _Handle = IntPtr.Zero;

    private static NativeCreateInterface? _CallCreateInterface;

    private static NativeSteamGetCallback? _CallSteamBGetCallback;

    private static NativeSteamFreeLastCallback? _CallSteamFreeLastCallback;

    private static Delegate? GetExportDelegate<TDelegate>(nint module, string name)
    {
        nint procAddress = Native.GetProcAddress(module, name);
        if (procAddress != IntPtr.Zero)
        {
            return Marshal.GetDelegateForFunctionPointer(procAddress, typeof(TDelegate));
        }
        return null;
    }

    private static TDelegate? GetExportFunction<TDelegate>(nint module, string name) where TDelegate : class
    {
        return (TDelegate?)(object?)GetExportDelegate<TDelegate>(module, name);
    }

    public static string? GetInstallPath()
    {
        string? val = (string?)Registry.GetValue("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath", null);
        if (!string.IsNullOrWhiteSpace(val)) return Path.GetFullPath(val.Trim().Replace('/', '\\'));

        val = (string?)Registry.GetValue("HKEY_LOCAL_MACHINE\\Software\\WOW6432Node\\Valve\\Steam", "InstallPath", null);
        if (!string.IsNullOrWhiteSpace(val)) return Path.GetFullPath(val.Trim().Replace('/', '\\'));

        val = (string?)Registry.GetValue("HKEY_LOCAL_MACHINE\\Software\\Valve\\Steam", "InstallPath", null);
        if (!string.IsNullOrWhiteSpace(val)) return Path.GetFullPath(val.Trim().Replace('/', '\\'));

        return null;
    }

    public static TClass? CreateInterface<TClass>(string version) where TClass : INativeWrapper, new()
    {
        if (_CallCreateInterface == null)
        {
            return default;
        }
        nint num = _CallCreateInterface(version, IntPtr.Zero);
        if (num == IntPtr.Zero)
        {
            return default;
        }
        TClass result = new TClass();
        result.SetupFunctions(num);
        return result;
    }

    public static bool GetCallback(int pipe, out CallbackMessage message, out int call)
    {
        if (_CallSteamBGetCallback == null)
        {
            message = default;
            call = 0;
            return false;
        }
        return _CallSteamBGetCallback(pipe, out message, out call);
    }

    public static bool FreeLastCallback(int pipe)
    {
        if (_CallSteamFreeLastCallback == null)
        {
            return false;
        }
        return _CallSteamFreeLastCallback(pipe);
    }

    public static bool Load()
    {
        if (_Handle != IntPtr.Zero)
        {
            return true;
        }
        string? installPath = GetInstallPath();
        if (installPath == null)
        {
            return false;
        }
        Native.SetDllDirectory(installPath);
        string binDir = Path.Combine(installPath, "bin");
        if (Directory.Exists(binDir))
        {
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(binDir, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", binDir + ";" + currentPath);
            }
        }
        installPath = Path.Combine(installPath, Environment.Is64BitProcess ? "steamclient64.dll" : "steamclient.dll");
        nint num = Native.LoadLibraryEx(installPath, IntPtr.Zero, 8u);
        if (num == IntPtr.Zero)
        {
            return false;
        }
        _CallCreateInterface = GetExportFunction<NativeCreateInterface>(num, "CreateInterface");
        if (_CallCreateInterface == null)
        {
            return false;
        }
        _CallSteamBGetCallback = GetExportFunction<NativeSteamGetCallback>(num, "Steam_BGetCallback");
        if (_CallSteamBGetCallback == null)
        {
            return false;
        }
        _CallSteamFreeLastCallback = GetExportFunction<NativeSteamFreeLastCallback>(num, "Steam_FreeLastCallback");
        if (_CallSteamFreeLastCallback == null)
        {
            return false;
        }
        _Handle = num;
        return true;
    }
}
