using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DataJackUIGui.Services.GameData;

public static partial class SteamAccount
{
    private const ulong SteamId64Base = 76561197960265728uL;

    [GeneratedRegex("\"(\\d{17})\"\\s*\\{([^}]*)\\}")]
    private static partial Regex UserBlock();

    [GeneratedRegex("\"(\\w+)\"\\s*\"([^\"]*)\"")]
    private static partial Regex Pair();

    public static uint? Current(string? steamPath)
    {
        if (steamPath == null)
        {
            return null;
        }
        try
        {
            string fullRoot = Path.GetFullPath(steamPath);
            string path = Path.GetFullPath(Path.Combine(fullRoot, "config", "loginusers.vdf"));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return File.Exists(path) ? FromLoginUsers(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static uint? FromLoginUsers(string vdf)
    {
        ulong? num = null;
        long num2 = long.MinValue;
        foreach (Match item in UserBlock().Matches(vdf))
        {
            ulong num3 = ulong.Parse(item.Groups[1].Value);
            var dictionary = Pair().Matches(item.Groups[2].Value)
                .ToDictionary(p => p.Groups[1].Value, p => p.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            if (dictionary.GetValueOrDefault("MostRecent") == "1")
            {
                return ToAccountId(num3);
            }
            long num4 = long.TryParse(dictionary.GetValueOrDefault("timestamp"), out var result) ? result : 0;
            if (num4 > num2)
            {
                num2 = num4;
                num = num3;
            }
        }
        if (num.HasValue)
        {
            return ToAccountId(num.Value);
        }
        return null;
    }

    private static uint? ToAccountId(ulong steamId64)
    {
        if (steamId64 <= SteamId64Base)
        {
            return null;
        }
        return (uint)(steamId64 - SteamId64Base);
    }
}
