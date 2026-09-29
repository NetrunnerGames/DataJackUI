using System.IO;
using System.Text.RegularExpressions;

namespace DataJackUIGui.Services;

/// <summary>
/// One depot/app entry declared in a lua file via addappid / setManifestid.
/// <para>
/// <paramref name="ManifestId"/> is an ACTIVE version pin. <paramref name="CommentedManifestId"/> is a
/// pin that exists in the file but is commented out, which is the normal state after an install with
/// the "Auto Update Apps" setting on (see <see cref="LuaInstaller"/>). The two must stay distinct: a
/// commented pin means "Steam keeps this updated", not "pinned to this manifest".
/// </para>
/// </summary>
/// <param name="Key">
/// The depot decryption key from <c>addappid(id, 1, "key")</c>, or null for a bare <c>addappid(id)</c>
/// (a DLC entitlement rather than a content depot). Carried, not just counted, because the depot
/// downloader needs the actual key bytes to decrypt CDN chunks.
/// </param>
public record LuaEntry(long Id, string? Key, string? ManifestId, string? CommentedManifestId, string? Comment)
{
    /// <summary>True when this entry carries a decryption key, i.e. it's a content depot.</summary>
    public bool HasKey => Key is not null;

    /// <summary>
    /// Size on disk from <c>setManifestid(id, "gid", size)</c>'s third argument, or null when the line
    /// omits it. The only size available for a depot Steam's app info does not list.
    /// </summary>
    public long? SizeOnDisk { get; init; }
}

/// <summary>Parsed contents of a stplug-in lua file.
/// <para>
/// <paramref name="Entries"/> is what the lua ACTIVELY declares. <paramref name="DisabledEntries"/> are
/// ids whose addappid line exists but is commented out. Switched off from the Builds page. They're kept
/// out of Entries so the install-time before/after diff still means "what this lua unlocks", but the
/// Builds page needs them to keep showing a switched-off depot (otherwise disabling one would make its
/// row vanish from "In lua", taking the switch with it).
/// </para>
/// </summary>
public record LuaContents(
    long BaseAppId,
    IReadOnlyList<LuaEntry> Entries,
    IReadOnlyDictionary<long, string> ActivePins,
    IReadOnlyDictionary<long, string> CommentedPins,
    IReadOnlyList<LuaEntry> DisabledEntries)
{
    public int DepotCount => Entries.Count(e => e.HasKey);
    public int DlcCount => Entries.Count(e => !e.HasKey && e.Id != BaseAppId);

    /// <summary>True when this lua pins at least one depot to a fixed manifest, i.e. it's locked to a
    /// specific build rather than tracking whatever Steam ships.</summary>
    public bool HasActivePins => ActivePins.Count > 0;
}

/// <summary>Difference between an old and new lua: ids the new one adds and ids it removes.</summary>
public record LuaDiff(IReadOnlyList<LuaEntry> Added, IReadOnlyList<LuaEntry> Removed)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// Reads the addappid()/setManifestid() lines from a stplug-in lua file so the detail view can
/// show what a game actually unlocks (base app, DLC ids, depot ids + whether a key is present).
/// </summary>
public static partial class LuaFileParser
{
    // Matched against ONE line at a time (see Parse), so \s can't cross newlines and collapse
    // multiple addappid lines into a single match.
    // addappid(123)                    -> id only
    // addappid(123, 1, "key")  -- Name -> id + key + optional trailing comment
    [GeneratedRegex(@"addappid\s*\(\s*(\d+)\s*(?:,\s*\d+\s*(?:,\s*""([^""]*)"")?)?\s*\)[ \t]*(?:--[ \t]*(.*?)[ \t]*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AddAppIdRegex();

    // setManifestid(depotid, "manifestid", size) — the third argument is optional and is the depot's
    // size on disk, which is the only size available for a depot Steam's app info never mentions.
    [GeneratedRegex(@"setManifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""\s*(?:,\s*(\d+))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex SetManifestRegex();

    // Strips a trailing "(123456) ..." / "デポ" tail that Hubcap appends to depot comments.
    [GeneratedRegex(@"\s*\(\d+\)\s*\S*\s*$")]
    private static partial Regex CommentTailRegex();

    /// <summary>Tidy a raw lua trailing comment into a display name (drops Hubcap's "(id) デポ" tail).</summary>
    private static string? CleanComment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        // Reject commented-out code (e.g. "setManifestid(...)", "addappid(...)"), not a human name.
        if (s.Contains("setManifestid", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("addappid", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("addtoken", StringComparison.OrdinalIgnoreCase))
            return null;
        s = CommentTailRegex().Replace(s, "").Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static LuaContents? Parse(string filePath, long appIdFromName)
    {
        try
        {
            string text = File.ReadAllText(filePath);

            var state = new ParserState();
            
            foreach (string rawLine in text.Split('\n'))
            {
                ProcessLine(rawLine, state);
            }

            var entries = state.Order
                .Select(id => new LuaEntry(id, state.KeyById[id],
                    state.Manifests.TryGetValue(id, out var mid) ? mid : null,
                    state.CommentedManifests.TryGetValue(id, out var cmid) ? cmid : null,
                    state.CommentById.TryGetValue(id, out var c) ? c : null)
                { SizeOnDisk = state.Sizes.TryGetValue(id, out long sz) ? sz : null })
                .ToList();

            var disabled = state.DisabledOrder
                .Where(id => !state.KeyById.ContainsKey(id))
                .Select(id => new LuaEntry(id, state.DisabledKeyById[id],
                    state.Manifests.TryGetValue(id, out var dmid) ? dmid : null,
                    state.CommentedManifests.TryGetValue(id, out var dcmid) ? dcmid : null,
                    state.CommentById.TryGetValue(id, out var dc) ? dc : null)
                { SizeOnDisk = state.Sizes.TryGetValue(id, out long dsz) ? dsz : null })
                .ToList();

            long baseId = entries.Count > 0 ? entries[0].Id : appIdFromName;
            return new LuaContents(baseId, entries, state.Manifests, state.CommentedManifests, disabled);
        }
        catch
        {
            return null;
        }
    }

    private class ParserState
    {
        public List<long> Order { get; } = new();
        public Dictionary<long, string?> KeyById { get; } = new();
        public Dictionary<long, string> CommentById { get; } = new();
        public Dictionary<long, string> Manifests { get; } = new();
        public Dictionary<long, long> Sizes { get; } = new();
        public Dictionary<long, string> CommentedManifests { get; } = new();
        public List<long> DisabledOrder { get; } = new();
        public Dictionary<long, string?> DisabledKeyById { get; } = new();
    }

    private static void ProcessLine(string rawLine, ParserState state)
    {
        string line = rawLine.Trim();
        bool commented = line.StartsWith("--");

        var pin = SetManifestRegex().Match(line);
        if (pin.Success && long.TryParse(pin.Groups[1].Value, out long depot))
        {
            (commented ? state.CommentedManifests : state.Manifests)[depot] = pin.Groups[2].Value;

            if (pin.Groups[3].Success && long.TryParse(pin.Groups[3].Value, out long sz) && sz > 0)
                state.Sizes.TryAdd(depot, sz);
        }

        var m = AddAppIdRegex().Match(commented ? line.TrimStart('-', ' ') : line);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out long id)) return;

        string? key = m.Groups[2].Success && !string.IsNullOrEmpty(m.Groups[2].Value) ? m.Groups[2].Value : null;

        string? comment = CleanComment(m.Groups[3].Success ? m.Groups[3].Value : null);
        if (comment is not null && (!state.CommentById.TryGetValue(id, out var prev) || comment.Length > prev.Length))
            state.CommentById[id] = comment;

        MergeEntry(state, id, key, commented);
    }

    private static void MergeEntry(ParserState state, long id, string? key, bool commented)
    {
        if (commented)
        {
            if (state.DisabledKeyById.TryGetValue(id, out var hadKey)) state.DisabledKeyById[id] = hadKey ?? key;
            else { state.DisabledKeyById[id] = key; state.DisabledOrder.Add(id); }
            return;
        }
        if (state.KeyById.TryGetValue(id, out var existing))
            state.KeyById[id] = existing ?? key;
        else { state.KeyById[id] = key; state.Order.Add(id); }
    }

    /// <summary>Diff old vs new lua by entry id: what the new lua adds and what it removes.</summary>
    public static LuaDiff Diff(LuaContents? oldLua, LuaContents newLua)
    {
        var oldIds = oldLua?.Entries.Select(e => e.Id).ToHashSet() ?? [];
        var newIds = newLua.Entries.Select(e => e.Id).ToHashSet();

        var added = newLua.Entries.Where(e => !oldIds.Contains(e.Id)).ToList();
        var removed = oldLua?.Entries.Where(e => !newIds.Contains(e.Id)).ToList() ?? [];
        return new LuaDiff(added, removed);
    }
}
