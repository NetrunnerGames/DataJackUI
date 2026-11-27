using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace DataJackUIGui.Services.GameData;

public class PlaytimeService
{
    private readonly record struct Token(char Kind, int Start, int Length);



    private static readonly string[] AppsPath = new string[5] { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps" };

    private readonly Func<string?> _steamPath;

    private readonly Func<bool> _isSteamRunning;

    private string? ConfigPath
    {
        get
        {
            string? text = _steamPath();
            if (text != null)
            {
                // Resolve the active 32-bit Steam Account ID to locate userdata\<AccountID>\config\localconfig.vdf,
                // where Steam stores per-game local playtime records.
                uint? num = SteamAccount.Current(text);
                if (num.HasValue)
                {
                    uint valueOrDefault = num.GetValueOrDefault();
                    return Path.Combine(text, "userdata", valueOrDefault.ToString(), "config", "localconfig.vdf");
                }
            }
            return null;
        }
    }

    public PlaytimeService(SteamService steam)
        : this(() => steam.EffectivePath, SteamService.IsSteamRunning)
    {
    }

    internal PlaytimeService(Func<string?> steamPath, Func<bool> isSteamRunning)
    {
        _steamPath = steamPath;
        _isSteamRunning = isSteamRunning;
    }

    public int? GetMinutes(long appId)
    {
        try
        {
            string? configPath = ConfigPath;
            if (configPath == null || !File.Exists(configPath))
            {
                return null;
            }
            return GetMinutes(Decode(File.ReadAllBytes(configPath)).Text, appId);
        }
        catch
        {
            return null;
        }
    }

    public string? SetMinutes(long appId, int minutes)
    {
        if (_isSteamRunning())
        {
            return "Steam is running; it would overwrite the file on exit.";
        }
        string? configPath = ConfigPath;
        if (configPath == null || !File.Exists(configPath))
        {
            return "Steam's localconfig.vdf wasn't found.";
        }
        try
        {
            (string Text, byte[] Bom) tuple = Decode(File.ReadAllBytes(configPath));
            string item = tuple.Text;
            byte[] item2 = tuple.Bom;
            string s = SetMinutes(item, appId, Math.Max(0, minutes)) ?? throw new InvalidDataException("No apps section in localconfig.vdf.");
            string text = configPath + ".bak";
            if (!File.Exists(text))
            {
                File.Copy(configPath, text);
            }
            string text2 = configPath + ".tmp";
            byte[] array = item2;
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            int num = 0;
            byte[] array2 = new byte[array.Length + bytes.Length];
            ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array);
            readOnlySpan.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan.Length));
            num += readOnlySpan.Length;
            ReadOnlySpan<byte> readOnlySpan2 = new ReadOnlySpan<byte>(bytes);
            readOnlySpan2.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan2.Length));
            num += readOnlySpan2.Length;
            File.WriteAllBytes(text2, array2);
            File.Move(text2, configPath, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    internal static int? GetMinutes(string vdf, long appId)
    {
        (int, int)? tuple = FindApp(vdf, appId);
        if (tuple.HasValue)
        {
            (int, int) valueOrDefault = tuple.GetValueOrDefault();
            (int, int)? tuple2 = FindValue(vdf, valueOrDefault.Item1, valueOrDefault.Item2, "Playtime");
            if (tuple2.HasValue)
            {
                (int, int) valueOrDefault2 = tuple2.GetValueOrDefault();
                if (int.TryParse(vdf.AsSpan(valueOrDefault2.Item1, valueOrDefault2.Item2), out var result))
                {
                    return result;
                }
            }
            return null;
        }
        return null;
    }

    internal static string? SetMinutes(string vdf, long appId, int minutes)
    {
        string text = minutes.ToString();
        (int, int)? tuple = FindApp(vdf, appId);
        if (tuple.HasValue)
        {
            (int, int) valueOrDefault = tuple.GetValueOrDefault();
            (int, int)? tuple2 = FindValue(vdf, valueOrDefault.Item1, valueOrDefault.Item2, "Playtime");
            if (tuple2.HasValue)
            {
                (int, int) valueOrDefault2 = tuple2.GetValueOrDefault();
                return vdf.Substring(0, valueOrDefault2.Item1) + text + vdf.Substring(valueOrDefault2.Item1 + valueOrDefault2.Item2);
            }
            string text2 = IndentBefore(vdf, valueOrDefault.Item2) + "\t";
            return vdf.Insert(LineStart(vdf, valueOrDefault.Item2), text2 + "\"Playtime\"\t\t\"" + text + "\"\n");
        }
        tuple = FindBlock(vdf, AppsPath);
        if (tuple.HasValue)
        {
            (int, int) valueOrDefault3 = tuple.GetValueOrDefault();
            string value = IndentBefore(vdf, valueOrDefault3.Item2) + "\t";
            return vdf.Insert(LineStart(vdf, valueOrDefault3.Item2), $"{value}\"{appId}\"\n{value}{{\n{value}\t\"Playtime\"\t\t\"{text}\"\n{value}}}\n");
        }
        return null;
    }

    private static (int Open, int Close)? FindApp(string vdf, long appId)
    {
        string[] appsPath = AppsPath;
        int num = 0;
        string[] array = new string[1 + appsPath.Length];
        ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(appsPath);
        readOnlySpan.CopyTo(new Span<string>(array).Slice(num, readOnlySpan.Length));
        num += readOnlySpan.Length;
        array[num] = appId.ToString();
        return FindBlock(vdf, array);
    }

    private static (int Open, int Close)? FindBlock(string vdf, string[] path)
    {
        List<string> list = new List<string>();
        string? text = null;
        int num = -1;
        foreach (Token item in Tokens(vdf))
        {
            if (item.Kind == '{')
            {
                list.Add(text ?? "");
                text = null;
                if (num < 0 && Matches(list, path))
                {
                    num = item.Start;
                }
            }
            else if (item.Kind == '}')
            {
                if (num >= 0 && Matches(list, path))
                {
                    return (num, item.Start);
                }
                if (list.Count > 0)
                {
                    list.RemoveAt(list.Count - 1);
                }
                text = null;
            }
            else
            {
                text = ((text == null) ? vdf.Substring(item.Start, item.Length) : null);
            }
        }
        return null;
    }

    private static (int Start, int Length)? FindValue(string vdf, int open, int close, string key)
    {
        int num = 0;
        bool flag = false;
        bool flag2 = false;
        foreach (Token item in Tokens(vdf))
        {
            if (item.Start <= open)
            {
                continue;
            }
            if (item.Start >= close)
            {
                break;
            }
            if (item.Kind == '{')
            {
                num++;
                flag = false;
            }
            else if (item.Kind == '}')
            {
                num--;
            }
            else
            {
                if (num > 0)
                {
                    continue;
                }
                if (flag)
                {
                    if (flag2)
                    {
                        return (item.Start, item.Length);
                    }
                    flag = false;
                }
                else
                {
                    flag2 = string.Equals(vdf.Substring(item.Start, item.Length), key, StringComparison.OrdinalIgnoreCase);
                    flag = true;
                }
            }
        }
        return null;
    }

    private static bool Matches(List<string> stack, string[] path)
    {
        if (stack.Count == path.Length)
        {
            return stack.Zip(path).All(((string First, string Second) p) => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    private static IEnumerable<Token> Tokens(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '{' || c == '}')
            {
                yield return new Token(c, i, 1);
                i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (c == '"')
            {
                int start = ++i;
                while (i < s.Length && s[i] != '"')
                {
                    i += (s[i] != '\\') ? 1 : 2;
                }
                yield return new Token('"', start, Math.Min(i, s.Length) - start);
                if (i < s.Length) i++;
                continue;
            }
            i++;
        }
    }

    private static int LineStart(string s, int pos)
    {
        return s.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
    }

    private static string IndentBefore(string s, int pos)
    {
        int num = LineStart(s, pos);
        int num2 = num;
        while (true)
        {
            bool flag = num2 < s.Length;
            if (flag)
            {
                char c = s[num2];
                bool flag2 = ((c == '\t' || c == ' ') ? true : false);
                flag = flag2;
            }
            if (!flag)
            {
                break;
            }
            num2++;
        }
        int num3 = num;
        return s.Substring(num3, num2 - num3);
    }

    private static (string Text, byte[] Bom) Decode(byte[] bytes)
    {
        bool flag = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191;
        return (Text: Encoding.UTF8.GetString(bytes, flag ? 3 : 0, bytes.Length - (flag ? 3 : 0)), Bom: (!flag) ? Array.Empty<byte>() : new byte[3] { 239, 187, 191 });
    }
}
