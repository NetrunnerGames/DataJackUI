using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SAM.API;

internal class NativeStrings
{
    public sealed class StringHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public nint Handle => handle;

        internal StringHandle(nint preexistingHandle, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(preexistingHandle);
        }

        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(handle);
                handle = IntPtr.Zero;
                return true;
            }
            return false;
        }
    }

    public static StringHandle StringToStringHandle(string? value)
    {
        if (value == null)
        {
            return new StringHandle(IntPtr.Zero, ownsHandle: true);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int length = bytes.Length;
        nint nativeMemory = Marshal.AllocHGlobal(length + 1);
        Marshal.Copy(bytes, 0, nativeMemory, length);
        Marshal.WriteByte(nativeMemory + length, 0); // Null-terminate

        return new StringHandle(nativeMemory, ownsHandle: true);
    }

    public static string? PointerToString(nint nativeData)
    {
        if (nativeData == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.PtrToStringUTF8(nativeData);
    }

    public static string? PointerToString(nint nativeData, int length)
    {
        if (nativeData == IntPtr.Zero)
        {
            return null;
        }

        if (length <= 0)
        {
            return string.Empty;
        }

        return Marshal.PtrToStringUTF8(nativeData, length);
    }
}
