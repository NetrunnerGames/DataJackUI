using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DataJackUIGui.Services;

/// <summary>
/// Autonomous binary pattern synthesizer that inspects Steam client modules (e.g. steamclient64.dll)
/// using fast Span searches and prologue extraction to synthesize verified AOB pattern signatures
/// with wildcarded volatile offsets.
/// </summary>
public static class PatternSynthesizerService
{
    private static readonly Dictionary<string, byte[]> s_knownTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ConfigStoreGetBinary"] = ParsePattern("40 53 55 56 57 48 83 EC 38 48 63 FA 49 8B E9"),
        ["CheckAppOwnership"] = ParsePattern("48 8B C4 89 50 10 48 89 48 08 55 53"),
        ["GetPackageInfo"] = ParsePattern("48 89 5C 24 18 89 54 24 10 55 56 57 48 83 EC 20 44 8B 49 20"),
        ["MarkLicenseAsChanged"] = ParsePattern("48 89 5C 24 20 89 54 24 10 55 56 57 48 83 EC 20"),
        ["ProcessPendingLicenseUpdates"] = ParsePattern("41 56 41 57 48 83 EC 38"),
        ["CUtlMemoryGrow"] = ParsePattern("48 89 5C 24 10 57 48 83 EC 30 8B FA 48 8B D9 8B 51 08 8B 49 10 8D 04 39")
    };

    public static Dictionary<string, List<string>> SynthesizeAllPatterns(string dllPath)
    {
        var results = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(dllPath)) return results;

        byte[] bytes = File.ReadAllBytes(dllPath);
        ReadOnlySpan<byte> span = bytes;

        foreach (var (name, target) in s_knownTargets)
        {
            int offset = span.IndexOf(target);
            if (offset >= 0)
            {
                var candidates = new List<string>();
                int len = Math.Min(Math.Max(16, target.Length), bytes.Length - offset);
                var slice = span.Slice(offset, len);

                string exact = ToHexString(slice);
                string wildcard = ToWildcardString(slice);

                candidates.Add(exact);
                if (exact != wildcard)
                {
                    candidates.Add(wildcard);
                }

                results[name] = candidates;
            }
        }

        return results;
    }

    public static bool UpdateOtaRepository(string otaRepoJsonPath, string steamClientDllPath)
    {
        try
        {
            var synthesized = SynthesizeAllPatterns(steamClientDllPath);
            if (synthesized.Count == 0) return false;

            var rootObj = new Dictionary<string, object>();
            rootObj["version"] = 1;
            rootObj["updated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var patternsDict = new Dictionary<string, Dictionary<string, List<string>>>();
            var steamClientDict = new Dictionary<string, List<string>>();

            var steamuiDict = new Dictionary<string, List<string>>
            {
                ["ShouldShowAppInLibrary"] = new List<string> { "40 53 48 83 EC 20 48 8B 01 48 8B D9 FF 10 3D D6 0C 09 00" }
            };

            foreach (var kv in synthesized)
            {
                steamClientDict[kv.Key] = kv.Value;
            }

            patternsDict["steamui"] = steamuiDict;
            patternsDict["steamclient64"] = steamClientDict;
            rootObj["patterns"] = patternsDict;

            var options = new JsonSerializerOptions { WriteIndented = true };
            string updatedJson = JsonSerializer.Serialize(rootObj, options);
            File.WriteAllText(otaRepoJsonPath, updatedJson);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ParsePattern(string pat)
    {
        string[] parts = pat.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        byte[] result = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            result[i] = Convert.ToByte(parts[i], 16);
        }
        return result;
    }

    private static string ToHexString(ReadOnlySpan<byte> span)
    {
        var sb = new StringBuilder(span.Length * 3);
        for (int i = 0; i < span.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(span[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static string ToWildcardString(ReadOnlySpan<byte> span)
    {
        var sb = new StringBuilder(span.Length * 3);
        for (int i = 0; i < span.Length; i++)
        {
            if (i > 0) sb.Append(' ');

            if (i > 0 && span[i - 1] == 0xEC && i >= 2 && span[i - 2] == 0x83)
            {
                sb.Append("??");
            }
            else if (i > 0 && span[i - 1] == 0x24 && i >= 2 && span[i - 2] == 0x5C)
            {
                sb.Append("??");
            }
            else if (i > 0 && span[i - 1] == 0x50 && i >= 2 && span[i - 2] == 0x89)
            {
                sb.Append("??");
            }
            else
            {
                sb.Append(span[i].ToString("X2"));
            }
        }
        return sb.ToString();
    }
}
