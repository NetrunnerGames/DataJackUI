using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SAM.Game;

internal static class StreamHelpers
{
    public static byte ReadValueU8(this Stream stream)
    {
        return (byte)stream.ReadByte();
    }

    public static int ReadValueS32(this Stream stream)
    {
        byte[] array = new byte[4];
        stream.ReadExactly(array, 0, 4);
        return BitConverter.ToInt32(array, 0);
    }

    public static uint ReadValueU32(this Stream stream)
    {
        byte[] array = new byte[4];
        stream.ReadExactly(array, 0, 4);
        return BitConverter.ToUInt32(array, 0);
    }

    public static ulong ReadValueU64(this Stream stream)
    {
        byte[] array = new byte[8];
        stream.ReadExactly(array, 0, 8);
        return BitConverter.ToUInt64(array, 0);
    }

    public static long ReadValueS64(this Stream stream)
    {
        byte[] array = new byte[8];
        stream.ReadExactly(array, 0, 8);
        return BitConverter.ToInt64(array, 0);
    }

    public static float ReadValueF32(this Stream stream)
    {
        byte[] array = new byte[4];
        stream.ReadExactly(array, 0, 4);
        return BitConverter.ToSingle(array, 0);
    }

    internal static string ReadStringInternalDynamic(this Stream stream, Encoding encoding, char end)
    {
        int byteCount = encoding.GetByteCount("e");
        string text = end.ToString(CultureInfo.InvariantCulture);
        int num = 0;
        byte[] array = new byte[128 * byteCount];
        while (true)
        {
            if (num + byteCount > array.Length)
            {
                Array.Resize(ref array, array.Length + (128 * byteCount));
            }
            int bytesRead = stream.Read(array, num, byteCount);
            if (bytesRead < byteCount)
            {
                if (bytesRead == 0 && num == 0) return "";
                break;
            }
            if (encoding.GetString(array, num, byteCount) == text)
            {
                break;
            }
            num += byteCount;
        }
        if (num == 0)
        {
            return "";
        }
        return encoding.GetString(array, 0, num);
    }

    public static string ReadStringAscii(this Stream stream)
    {
        return stream.ReadStringInternalDynamic(Encoding.ASCII, '\0');
    }

    public static string ReadStringUnicode(this Stream stream)
    {
        return stream.ReadStringInternalDynamic(Encoding.UTF8, '\0');
    }
}
