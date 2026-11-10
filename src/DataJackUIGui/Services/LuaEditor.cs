using System.Text.RegularExpressions;

namespace DataJackUIGui.Services;

/// <summary>
/// Toggles individual depot lines in a lua's TEXT, for the Builds page's per-depot switches.
///
/// <para>
/// Both switches work by commenting a line in or out. That's the only mechanism the lua format has,
/// and it's exactly what "Auto Update Apps" already does to manifest pins:
/// </para>
/// <list type="bullet">
/// <item><b>Lock</b>. <c>setManifestid(depot, "…")</c>. Active = pinned to that manifest. Commented out =
/// Steam is free to update the depot.</item>
/// <item><b>Enable</b>. <c>addappid(depot, 1, "key")</c>. Active = the decryption key applies. Commented
/// out = the depot isn't unlocked at all.</item>
/// </list>
///
/// <para>
/// Pure string→string so it's testable without touching Steam, and so a failed edit can never leave a
/// half-written file: the caller writes the returned text in one go.
/// </para>
/// </summary>
public static class LuaEditor
{
    // A line's leading "--" (with optional space), captured so it can be stripped or re-added while
    // preserving the original indentation.
    private const string CommentPrefix = @"^(?<indent>\s*)(?<comment>--\s*)?";

    /// <summary>Matches setManifestid(&lt;depot&gt;, …. Commented or not.</summary>
    private static Regex PinLine(long depotId) =>
        new(CommentPrefix + @"(?<body>setManifestid\s*\(\s*" + depotId + @"\s*[,)])",
            RegexOptions.IgnoreCase);

    /// <summary>Matches addappid(&lt;depot&gt;…. Commented or not, keyed or bare.</summary>
    private static Regex AddAppIdLine(long depotId) =>
        new(CommentPrefix + @"(?<body>addappid\s*\(\s*" + depotId + @"\s*[,)])",
            RegexOptions.IgnoreCase);

    /// <summary>Lock (pin) or unlock a depot by commenting its setManifestid line in/out.</summary>
    public static string SetDepotLocked(string lua, long depotId, bool locked) =>
        Rewrite(lua, PinLine(depotId), active: locked);

    /// <summary>Matches setManifestid(&lt;depot&gt;, …. Commented or not.</summary>
    private static Regex ManifestLine(long depotId) =>
        new(CommentPrefix + @"setManifestid\s*\(\s*" + depotId + @"\s*[,)][^)]*\)(?<tail>.*)$",
            RegexOptions.IgnoreCase);

    /// <summary>
    /// Set a depot to a specific manifest ID in the lua text.
    /// If a setManifestid line already exists for this depot (active or commented out),
    /// updates its manifest ID and ensures it is active (uncommented).
    /// If no setManifestid line exists, inserts one directly after the depot's addappid line,
    /// or appends it to the end if no addappid line exists.
    /// </summary>
    public static string SetDepotManifest(string lua, long depotId, string manifestId)
    {
        if (string.IsNullOrWhiteSpace(manifestId)) return lua;
        manifestId = manifestId.Trim('"', ' ');

        var lines = lua.Split('\n');
        var pinRegex = ManifestLine(depotId);
        bool foundPin = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var m = pinRegex.Match(lines[i]);
            if (!m.Success) continue;

            foundPin = true;
            string indent = m.Groups["indent"].Value;
            string tail = m.Groups["tail"].Value;
            lines[i] = $"{indent}setManifestid({depotId}, \"{manifestId}\"){tail}";
        }

        if (foundPin)
        {
            return string.Join('\n', lines);
        }

        // If no setManifestid existed, look for the depot's addappid line to insert right after it
        var list = new List<string>(lines);
        var addAppIdRegex = AddAppIdLine(depotId);
        int insertIndex = -1;
        string matchedIndent = "";

        for (int i = 0; i < list.Count; i++)
        {
            var m = addAppIdRegex.Match(list[i]);
            if (m.Success)
            {
                insertIndex = i + 1;
                matchedIndent = m.Groups["indent"].Value;
            }
        }

        bool hasCr = lua.Contains("\r\n");
        string cr = hasCr ? "\r" : "";

        if (insertIndex >= 0)
        {
            list.Insert(insertIndex, $"{matchedIndent}setManifestid({depotId}, \"{manifestId}\"){cr}");
        }
        else
        {
            if (list.Count > 0 && string.IsNullOrWhiteSpace(list[^1]))
            {
                list.Insert(list.Count - 1, $"setManifestid({depotId}, \"{manifestId}\"){cr}");
            }
            else
            {
                list.Add($"setManifestid({depotId}, \"{manifestId}\"){cr}");
            }
        }

        return string.Join('\n', list);
    }

    /// <summary>Enable or disable a depot by commenting its addappid (decryption key) line in/out.</summary>
    public static string SetDepotEnabled(string lua, long depotId, bool enabled) =>
        Rewrite(lua, AddAppIdLine(depotId), active: enabled);

    /// <summary>
    /// Comment in/out every line matching <paramref name="line"/>. An id can legitimately appear on more
    /// than one line (e.g. a bare <c>addappid(id)</c> plus a keyed <c>addappid(id, 1, "key")</c>), and
    /// leaving one of them behind would half-apply the toggle, so all matches are rewritten.
    /// </summary>
    private static string Rewrite(string lua, Regex line, bool active)
    {
        // Split on '\n' only, and put it back the same way, so CRLF files keep their '\r' untouched
        // (it rides along at the end of each element).
        var lines = lua.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var m = line.Match(lines[i]);
            if (!m.Success) continue;

            bool isCommented = m.Groups["comment"].Success;
            if (isCommented == !active) continue; // already in the requested state

            lines[i] = active
                // Uncomment: drop just the leading "--" run, keep everything after it.
                ? m.Groups["indent"].Value + lines[i][(m.Groups["comment"].Index + m.Groups["comment"].Length)..]
                // Comment: insert "--" after the indent.
                : m.Groups["indent"].Value + "--" + lines[i][m.Groups["indent"].Length..];
        }

        return string.Join('\n', lines);
    }
}
