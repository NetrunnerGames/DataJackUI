using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SAM.Game;

internal class KeyValue
{
    private static readonly KeyValue _Invalid = new KeyValue();

    public string Name = "<root>";

    public KeyValueType Type;

    public object? Value;

    public bool Valid;

    public List<KeyValue>? Children;

    public KeyValue this[string key]
    {
        get
        {
            if (Children == null)
            {
                return _Invalid;
            }
            KeyValue? keyValue = Children.SingleOrDefault((KeyValue c) => string.Compare(c.Name, key, StringComparison.InvariantCultureIgnoreCase) == 0);
            if (keyValue == null)
            {
                return _Invalid;
            }
            return keyValue;
        }
    }

    public string AsString(string defaultValue)
    {
        if (!Valid)
        {
            return defaultValue;
        }
        if (Value == null)
        {
            return defaultValue;
        }
        return Value.ToString() ?? defaultValue;
    }

    public int AsInteger(int defaultValue)
    {
        if (!Valid || Value == null)
        {
            return defaultValue;
        }
        switch (Type)
        {
            case KeyValueType.String:
            case KeyValueType.WideString:
                {
                    if (int.TryParse((string)Value, out var result))
                    {
                        return result;
                    }
                    return defaultValue;
                }
            case KeyValueType.Int32:
                return (int)Value;
            case KeyValueType.Float32:
                return (int)(float)Value;
            case KeyValueType.UInt64:
                return (int)((ulong)Value & 0xFFFFFFFFu);
            default:
                return defaultValue;
        }
    }

    public float AsFloat(float defaultValue)
    {
        if (!Valid || Value == null)
        {
            return defaultValue;
        }
        switch (Type)
        {
            case KeyValueType.String:
            case KeyValueType.WideString:
                {
                    if (float.TryParse((string)Value, out var result))
                    {
                        return result;
                    }
                    return defaultValue;
                }
            case KeyValueType.Int32:
                return (int)Value;
            case KeyValueType.Float32:
                return (float)Value;
            case KeyValueType.UInt64:
                return (ulong)Value & 0xFFFFFFFFu;
            default:
                return defaultValue;
        }
    }

    public bool AsBoolean(bool defaultValue)
    {
        if (!Valid || Value == null)
        {
            return defaultValue;
        }
        switch (Type)
        {
            case KeyValueType.String:
            case KeyValueType.WideString:
                {
                    if (int.TryParse((string)Value, out var result))
                    {
                        return result != 0;
                    }
                    return defaultValue;
                }
            case KeyValueType.Int32:
                return (int)Value != 0;
            case KeyValueType.Float32:
                return (int)(float)Value != 0;
            case KeyValueType.UInt64:
                return (ulong)Value != 0;
            default:
                return defaultValue;
        }
    }

    public override string ToString()
    {
        if (!Valid)
        {
            return "<invalid>";
        }
        if (Type == KeyValueType.None)
        {
            return Name;
        }
        return $"{Name} = {Value}";
    }

    public KeyValue? Find(string name)
    {
        return Children?.FirstOrDefault((KeyValue c) => string.Equals(c.Name, name, StringComparison.Ordinal));
    }

    public void WriteAsBinary(Stream output)
    {
        foreach (KeyValue item in Children ?? new List<KeyValue>())
        {
            output.WriteByte((byte)item.Type);
            WriteString(output, item.Name);
            switch (item.Type)
            {
                case KeyValueType.None:
                    item.WriteAsBinary(output);
                    break;
                case KeyValueType.String:
                    WriteString(output, (string)item.Value!);
                    break;
                case KeyValueType.Int32:
                    output.Write(BitConverter.GetBytes(Convert.ToInt32(item.Value)));
                    break;
                case KeyValueType.Float32:
                    output.Write(BitConverter.GetBytes(Convert.ToSingle(item.Value)));
                    break;
                case KeyValueType.Pointer:
                case KeyValueType.Color:
                    output.Write(BitConverter.GetBytes(Convert.ToUInt32(item.Value)));
                    break;
                case KeyValueType.UInt64:
                    output.Write(BitConverter.GetBytes(Convert.ToUInt64(item.Value)));
                    break;
                case KeyValueType.Int64:
                    output.Write(BitConverter.GetBytes(Convert.ToInt64(item.Value)));
                    break;
                default:
                    throw new FormatException($"cannot write {item.Type}");
            }
        }
        output.WriteByte(8);
    }

    public void SaveAsBinary(string path)
    {
        string text = path + ".tmp";
        using (FileStream output = File.Create(text))
        {
            WriteAsBinary(output);
        }
        File.Move(text, path, overwrite: true);
    }

    private static void WriteString(Stream output, string value)
    {
        output.Write(Encoding.UTF8.GetBytes(value ?? ""));
        output.WriteByte(0);
    }

    public static KeyValue? LoadAsBinary(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using FileStream input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            KeyValue keyValue = new KeyValue();
            if (!keyValue.ReadAsBinary(input))
            {
                return null;
            }
            return keyValue;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool ReadAsBinary(Stream input)
    {
        Children = new List<KeyValue>();
        try
        {
            while (true)
            {
                KeyValueType keyValueType = (KeyValueType)input.ReadValueU8();
                if (keyValueType == KeyValueType.End)
                {
                    break;
                }
                KeyValue keyValue = new KeyValue
                {
                    Type = keyValueType,
                    Name = input.ReadStringUnicode()
                };
                switch (keyValueType)
                {
                    case KeyValueType.None:
                        keyValue.ReadAsBinary(input);
                        break;
                    case KeyValueType.String:
                        keyValue.Valid = true;
                        keyValue.Value = input.ReadStringUnicode();
                        break;
                    case KeyValueType.WideString:
                        throw new FormatException("wstring is unsupported");
                    case KeyValueType.Int32:
                        keyValue.Valid = true;
                        keyValue.Value = input.ReadValueS32();
                        break;
                    case KeyValueType.UInt64:
                        keyValue.Valid = true;
                        keyValue.Value = input.ReadValueU64();
                        break;
                    case KeyValueType.Int64:
                        keyValue.Valid = true;
                        keyValue.Value = input.ReadValueS64();
                        break;
                    case KeyValueType.Float32:
                        keyValue.Valid = true;
                        keyValue.Value = input.ReadValueF32();
                        break;
                    case KeyValueType.Color:
                        keyValue.Valid = true;
                        keyValue.Value = input.ReadValueU32();
                        break;
                    case KeyValueType.Pointer:
                        keyValue.Valid = true;
                        keyValue.Value = input.ReadValueU32();
                        break;
                    default:
                        throw new FormatException();
                }
                if (input.Position >= input.Length)
                {
                    throw new FormatException();
                }
                Children.Add(keyValue);
            }
            Valid = true;
            return input.Position == input.Length;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
