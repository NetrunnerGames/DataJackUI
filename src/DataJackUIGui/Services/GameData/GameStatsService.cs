using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SAM.Game;

namespace DataJackUIGui.Services.GameData;

public class GameStatsService
{
    private sealed record Schema(List<AchievementInfo> Achievements, List<StatInfo> Stats, List<AchievementGroup> Groups);

    private const string IconBase = "https://shared.akamai.steamstatic.com/community_assets/images/apps/";

    private readonly Func<string?> _steamPath;

    private readonly Func<bool> _isSteamRunning;

    private readonly Action _stopSteam;

    private readonly Action _startSteam;

    private readonly Func<long, GameStatsEdit, CancellationToken, Task<StatsSaveResult>> _saveLive;

    private readonly Func<string> _language;

    private readonly Func<DateTimeOffset> _now;

    private string? StatsDir
    {
        get
        {
            string? text = _steamPath();
            if (text == null)
            {
                return null;
            }
            return Path.Combine(text, "appcache", "stats");
        }
    }

    private string? PendingEditsPath
    {
        get
        {
            string? text = _steamPath();
            if (text == null)
            {
                return null;
            }
            return Path.Combine(text, "cloud_redirect", "pending_edits.json");
        }
    }

    private uint? Account => SteamAccount.Current(_steamPath());

    public GameStatsService(SteamService steam, StatsHelperClient live)
        : this(() => steam.EffectivePath, SteamService.IsSteamRunning, steam.StopSteam, delegate
        {
            steam.StartSteam();
        }, live.SaveAsync, () => SteamService.SteamLanguage ?? "english", () => DateTimeOffset.UtcNow)
    {
    }

    internal GameStatsService(Func<string?> steamPath, Func<bool> isSteamRunning, Action stopSteam, Action startSteam, Func<long, GameStatsEdit, CancellationToken, Task<StatsSaveResult>> saveLive, Func<string> language, Func<DateTimeOffset> now)
    {
        _steamPath = steamPath;
        _isSteamRunning = isSteamRunning;
        _stopSteam = stopSteam;
        _startSteam = startSteam;
        _saveLive = saveLive;
        _language = language;
        _now = now;
    }

    private string? SchemaPath(long app)
    {
        string? statsDir = StatsDir;
        if (statsDir == null)
        {
            return null;
        }
        return Path.Combine(statsDir, $"UserGameStatsSchema_{app}.bin");
    }

    private string? UserPath(long app, uint acct)
    {
        string? statsDir = StatsDir;
        if (statsDir == null)
        {
            return null;
        }
        return Path.Combine(statsDir, $"UserGameStats_{acct}_{app}.bin");
    }

    private string? CloudRedirectPath(long app, uint acct)
    {
        string? text = _steamPath();
        if (text == null)
        {
            return null;
        }
        return Path.Combine(text, "cloud_redirect", "stats", acct.ToString(), $"{app}.json");
    }

    public GameStatsSnapshot Load(long appId)
    {
        uint? account = Account;
        Schema? schema = ReadSchema(appId);
        int num;
        if (account.HasValue)
        {
            uint valueOrDefault = account.GetValueOrDefault();
            string? text = CloudRedirectPath(appId, valueOrDefault);
            if (text != null)
            {
                num = (File.Exists(text) ? 1 : 0);
                goto IL_0040;
            }
        }
        num = 0;
        goto IL_0040;
    IL_0040:
        bool cloudRedirectManaged = (byte)num != 0;
        if ((object?)schema == null)
        {
            return new GameStatsSnapshot(appId, account, HasSchema: false, cloudRedirectManaged, Array.Empty<AchievementInfo>(), Array.Empty<StatInfo>(), Array.Empty<AchievementGroup>());
        }
        (Dictionary<int, uint>, Dictionary<int, double>, Dictionary<(int, int), uint>) tuple = ReadUser(appId, account);
        Dictionary<int, uint> bits = tuple.Item1;
        Dictionary<int, double> values = tuple.Item2;
        Dictionary<(int, int), uint> times = tuple.Item3;
        Dictionary<string, double> statValues = schema.Stats.DistinctBy((StatInfo s) => s.ApiName).ToDictionary((StatInfo s) => s.ApiName, (StatInfo s) => values.GetValueOrDefault(s.StatId, s.Value));
        List<AchievementInfo> achievements = schema.Achievements.Select(delegate (AchievementInfo d)
        {
            bool flag = ((bits.GetValueOrDefault(d.StatId) >> d.Bit) & 1) == 1;
            uint value;
            DateTimeOffset? unlockedAt = ((times.TryGetValue((d.StatId, d.Bit), out value) && value != 0) ? new DateTimeOffset?(DateTimeOffset.FromUnixTimeSeconds(value)) : ((DateTimeOffset?)null));
            AchievementInfo obj = d with
            {
                Unlocked = (flag || unlockedAt.HasValue),
                UnlockedAt = unlockedAt
            };
            AchievementProgress? progress = d.Progress;
            obj.Progress = (((object?)progress != null && statValues.TryGetValue(progress.StatName, out var value2)) ? progress with
            {
                Value = value2
            } : d.Progress);
            return obj;
        }).ToList();
        double value3;
        List<StatInfo> stats = schema.Stats.Select((StatInfo s) => (!values.TryGetValue(s.StatId, out value3)) ? s : s with
        {
            Value = value3
        }).ToList();
        return new GameStatsSnapshot(appId, account, HasSchema: true, cloudRedirectManaged, achievements, stats, schema.Groups);
    }

    private Schema? ReadSchema(long appId)
    {
        string? text = SchemaPath(appId);
        if (text != null)
        {
            KeyValue? keyValue = KeyValue.LoadAsBinary(text);
            if (keyValue != null)
            {
                KeyValue? kv = Child(keyValue, appId.ToString()) ?? keyValue.Children?.FirstOrDefault((KeyValue c) => Child(c, "stats") != null);
                KeyValue? keyValue2 = Child(kv, "stats");
                if (keyValue2?.Children == null)
                {
                    return null;
                }
                string lang = _language();
                int result;
                List<AchievementGroup> groups = (Child(kv, "groups")?.Children ?? new List<KeyValue>()).Where((KeyValue g) => int.TryParse(g.Name, out result)).Select(delegate (KeyValue g)
                {
                    int id = int.Parse(g.Name);
                    string name = Localized(Child(g, "name"), lang, "");
                    KeyValue? keyValue3 = Child(g, "dlcappid");
                    return new AchievementGroup(id, name, (keyValue3 != null && Int(keyValue3) > 0) ? new long?(Int(keyValue3)) : ((long?)null));
                }).ToList();
                List<AchievementInfo> list = new List<AchievementInfo>();
                List<StatInfo> list2 = new List<StatInfo>();
                foreach (KeyValue child in keyValue2.Children)
                {
                    if (!int.TryParse(child.Name, out var result2))
                    {
                        continue;
                    }
                    string text2 = Str(Child(child, "name"));
                    bool flag = (Int(Child(child, "permission")) & 3) != 0;
                    switch (StatType(child))
                    {
                        case 1:
                        case 2:
                        case 3:
                            list2.Add(new StatInfo(result2, text2, Localized(Child(Child(child, "display"), "name"), lang, text2), StatType(child) != 1, Num(Child(child, "default")), flag, Int(Child(child, "incrementonly")) != 0));
                            break;
                        case 4:
                        case 5:
                            foreach (KeyValue item in child.Children?.Where((KeyValue c) => c.Name.Equals("bits", StringComparison.OrdinalIgnoreCase)) ?? Array.Empty<KeyValue>())
                            {
                                foreach (KeyValue item2 in item.Children ?? new List<KeyValue>())
                                {
                                    int result3;
                                    bool flag2 = !int.TryParse(item2.Name, out result3);
                                    if (!flag2)
                                    {
                                        bool flag3 = ((result3 < 0 || result3 > 31) ? true : false);
                                        flag2 = flag3;
                                    }
                                    if (!flag2)
                                    {
                                        KeyValue? kv2 = Child(item2, "display");
                                        string text3 = Str(Child(item2, "name"));
                                        string text4 = Str(Child(kv2, "icon"));
                                        string text5 = Str(Child(kv2, "icon_gray"));
                                        int statId = result2;
                                        int bit = result3;
                                        string name2 = Localized(Child(kv2, "name"), lang, text3);
                                        string description = Localized(Child(kv2, "desc"), lang, "");
                                        bool hidden = Int(Child(kv2, "hidden")) != 0;
                                        bool @protected = (Int(Child(item2, "permission")) & 3) != 0 || flag;
                                        string? iconUrl = ((text4.Length > 0) ? $"{"https://shared.akamai.steamstatic.com/community_assets/images/apps/"}{appId}/{text4}" : null);
                                        string? iconGrayUrl = ((text5.Length > 0) ? $"{"https://shared.akamai.steamstatic.com/community_assets/images/apps/"}{appId}/{text5}" : null);
                                        KeyValue? keyValue4 = Child(item2, "groupid");
                                        list.Add(new AchievementInfo(text3, statId, bit, name2, description, hidden, @protected, iconUrl, iconGrayUrl, Unlocked: false, null, (keyValue4 != null) ? new int?(Int(keyValue4)) : ((int?)null), ReadProgress(Child(item2, "progress"))));
                                    }
                                }
                            }
                            break;
                    }
                }
                return new Schema(list, list2, groups);
            }
        }
        return null;
    }

    private static AchievementProgress? ReadProgress(KeyValue? progress)
    {
        KeyValue? keyValue = Child(progress, "value");
        if (keyValue == null || !Str(Child(keyValue, "operation")).Equals("statvalue", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string text = Str(Child(keyValue, "operand1"));
        double num = Num(Child(progress, "min_val"));
        double num2 = Num(Child(progress, "max_val"));
        if (text.Length <= 0 || !(num2 > num))
        {
            return null;
        }
        return new AchievementProgress(text, num, num2, num);
    }

    private static int StatType(KeyValue stat)
    {
        KeyValue? keyValue = Child(stat, "type");
        if (keyValue != null && keyValue.Type == KeyValueType.String)
        {
            switch (Str(keyValue).ToLowerInvariant())
            {
                case "integer":
                case "int":
                    return 1;
                case "float":
                    return 2;
                case "avgrate":
                case "averagerate":
                    return 3;
                case "achievements":
                    return 4;
                case "groupachievements":
                    return 5;
                default:
                    {
                        int result;
                        return int.TryParse(Str(keyValue), out result) ? result : 0;
                    }
            }
        }
        KeyValue? keyValue2 = Child(stat, "type_int");
        if (keyValue2 == null)
        {
            return Int(keyValue);
        }
        return Int(keyValue2);
    }

    private (Dictionary<int, uint> Bits, Dictionary<int, double> Values, Dictionary<(int, int), uint> Times) ReadUser(long appId, uint? acct)
    {
        Dictionary<int, uint> dictionary = new Dictionary<int, uint>();
        Dictionary<int, double> dictionary2 = new Dictionary<int, double>();
        Dictionary<(int, int), uint> dictionary3 = new Dictionary<(int, int), uint>();
        if (acct.HasValue)
        {
            uint valueOrDefault = acct.GetValueOrDefault();
            string? text = UserPath(appId, valueOrDefault);
            if (text != null)
            {
                foreach (KeyValue item in (KeyValue.LoadAsBinary(text)?.Find("cache"))?.Children ?? new List<KeyValue>())
                {
                    if (!int.TryParse(item.Name, out var result))
                    {
                        continue;
                    }
                    KeyValue? keyValue = item.Find("data");
                    if (keyValue != null)
                    {
                        dictionary[result] = ((keyValue.Type != KeyValueType.Float32) ? ((uint)Int(keyValue)) : 0u);
                        dictionary2[result] = Num(keyValue);
                    }
                    foreach (KeyValue item2 in item.Find("AchievementTimes")?.Children ?? new List<KeyValue>())
                    {
                        if (int.TryParse(item2.Name, out var result2))
                        {
                            dictionary3[(result, result2)] = (uint)Int(item2);
                        }
                    }
                }
                return (Bits: dictionary, Values: dictionary2, Times: dictionary3);
            }
        }
        return (Bits: dictionary, Values: dictionary2, Times: dictionary3);
    }

    public async Task<StatsSaveResult> SaveAsync(long appId, GameStatsEdit edit, CancellationToken ct = default(CancellationToken))
    {
        if (edit.Achievements.Any<KeyValuePair<string, bool>>((KeyValuePair<string, bool> e) => !e.Value) && IsCloudRedirectManaged(appId))
        {
            if (!_isSteamRunning())
            {
                return SaveToFile(appId, edit);
            }
            return await Task.Run(delegate
            {
                _stopSteam();
                try
                {
                    return SaveToFile(appId, edit);
                }
                finally
                {
                    _startSteam();
                }
            }, ct);
        }
        return (!_isSteamRunning()) ? SaveToFile(appId, edit) : (await SaveLiveAsync(appId, edit, ct));
    }

    private async Task<StatsSaveResult> SaveLiveAsync(long appId, GameStatsEdit edit, CancellationToken ct)
    {
        StatsSaveResult result = await _saveLive(appId, edit, ct);
        if (result.Outcome != StatsSaveOutcome.LiveRejected)
        {
            return result;
        }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(500, ct);
            }
            if (IsApplied(Load(appId), edit))
            {
                return new StatsSaveResult(StatsSaveOutcome.SavedLive);
            }
        }
        return result;
    }

    internal static bool IsApplied(GameStatsSnapshot snap, GameStatsEdit edit)
    {
        if (snap.HasSchema && edit.Achievements.All<KeyValuePair<string, bool>>((KeyValuePair<string, bool> e) => snap.Achievements.FirstOrDefault((AchievementInfo a) => a.ApiName == e.Key)?.Unlocked == e.Value))
        {
            return edit.Stats.All<KeyValuePair<string, double>>(delegate (KeyValuePair<string, double> e)
            {
                StatInfo? statInfo = snap.Stats.FirstOrDefault((StatInfo s) => s.ApiName == e.Key);
                return (object?)statInfo != null && ((!statInfo.IsFloat) ? (statInfo.Value == Math.Round(e.Value)) : ((float)statInfo.Value == (float)e.Value));
            });
        }
        return false;
    }

    public async Task<(bool Fetched, string? Error)> FetchSchemaAsync(long appId, CancellationToken ct = default(CancellationToken))
    {
        if (_isSteamRunning())
        {
            string? path = SchemaPath(appId);
            if (path != null)
            {
                StatsSaveResult statsSaveResult = await _saveLive(appId, new GameStatsEdit(new Dictionary<string, bool>(), new Dictionary<string, double>()), ct);
                if (statsSaveResult.Outcome != 0)
                {
                    return (Fetched: false, Error: statsSaveResult.Error ?? statsSaveResult.Outcome.ToString());
                }
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5.0);
                while (!File.Exists(path) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250, ct);
                }
                return (Fetched: File.Exists(path), Error: null);
            }
        }
        return (Fetched: false, Error: null);
    }

    private bool IsCloudRedirectManaged(long appId)
    {
        uint? account = Account;
        if (account.HasValue)
        {
            uint valueOrDefault = account.GetValueOrDefault();
            string? text = CloudRedirectPath(appId, valueOrDefault);
            if (text != null)
            {
                return File.Exists(text);
            }
        }
        return false;
    }

    public StatsSaveResult SaveToFile(long appId, GameStatsEdit edit)
    {
        if (_isSteamRunning())
        {
            return new StatsSaveResult(StatsSaveOutcome.Failed, "Steam is running; it would overwrite the file on exit.");
        }
        uint? account = Account;
        if (account.HasValue)
        {
            uint valueOrDefault = account.GetValueOrDefault();
            string? text = UserPath(appId, valueOrDefault);
            if (text != null)
            {
                Schema? schema = ReadSchema(appId);
                if ((object?)schema == null)
                {
                    return new StatsSaveResult(StatsSaveOutcome.Failed, "No achievement schema for this game.");
                }
                Dictionary<string, AchievementInfo> dictionary = schema.Achievements.ToDictionary<AchievementInfo, string>((AchievementInfo a) => a.ApiName, StringComparer.Ordinal);
                Dictionary<string, StatInfo> dictionary2 = schema.Stats.ToDictionary<StatInfo, string>((StatInfo s) => s.ApiName, StringComparer.Ordinal);
                string? text2 = CloudRedirectPath(appId, valueOrDefault);
                string? text3 = ((text2 != null && File.Exists(text2)) ? text2 : null);
                try
                {
                    KeyValue keyValue;
                    if (File.Exists(text))
                    {
                        keyValue = KeyValue.LoadAsBinary(text) ?? throw new InvalidDataException("The stats file couldn't be read; left untouched.");
                        string text4 = text + ".bak";
                        if (!File.Exists(text4))
                        {
                            File.Copy(text, text4);
                        }
                    }
                    else
                    {
                        keyValue = Section("<root>");
                    }
                    KeyValue orAdd = GetOrAdd(keyValue, "cache", () => Section("cache"));
                    uint num = (uint)_now().ToUnixTimeSeconds();
                    string key;
                    foreach (KeyValuePair<string, bool> achievement in edit.Achievements)
                    {
                        achievement.Deconstruct(out key, out var value);
                        string key2 = key;
                        bool flag = value;
                        if (!dictionary.TryGetValue(key2, out var a2))
                        {
                            continue;
                        }
                        KeyValue orAdd2 = GetOrAdd(orAdd, a2.StatId.ToString(), () => Section(a2.StatId.ToString()));
                        KeyValue orAdd3 = GetOrAdd(orAdd2, "data", () => Leaf("data", KeyValueType.Int32, 0));
                        uint num2 = (uint)Int(orAdd3);
                        num2 = (flag ? (num2 | (uint)(1 << a2.Bit)) : (num2 & (uint)(~(1 << a2.Bit))));
                        orAdd3.Type = KeyValueType.Int32;
                        orAdd3.Value = (int)num2;
                        KeyValue? keyValue2 = orAdd2.Find("AchievementTimes");
                        string key3 = a2.Bit.ToString();
                        if (flag)
                        {
                            if (keyValue2 == null)
                            {
                                keyValue2 = Add(orAdd2, Section("AchievementTimes"));
                            }
                            if (keyValue2.Find(key3) == null)
                            {
                                Add(keyValue2, Leaf(key3, KeyValueType.Int32, (int)num));
                            }
                        }
                        else
                        {
                            keyValue2?.Children?.RemoveAll((KeyValue t) => t.Name == key3);
                        }
                    }
                    foreach (KeyValuePair<string, double> stat in edit.Stats)
                    {
                        stat.Deconstruct(out key, out var value2);
                        string key4 = key;
                        double num3 = value2;
                        if (dictionary2.TryGetValue(key4, out var s2))
                        {
                            KeyValue orAdd4 = GetOrAdd(GetOrAdd(orAdd, s2.StatId.ToString(), () => Section(s2.StatId.ToString())), "data", () => Leaf("data", s2.IsFloat ? KeyValueType.Float32 : KeyValueType.Int32, 0));
                            orAdd4.Value = ((orAdd4.Type == KeyValueType.Float32) ? ((object)(float)num3) : ((object)(int)Math.Round(num3)));
                        }
                    }
                    KeyValue? keyValue3 = orAdd.Find("PendingChanges");
                    if (keyValue3 != null)
                    {
                        keyValue3.Value = 0;
                    }
                    string? text5 = ((text3 == null) ? null : BuildCloudRedirect(text3, edit, dictionary, num));
                    string? text6 = ((text3 == null) ? null : BuildPendingEdits(valueOrDefault, appId, edit, dictionary));
                    keyValue.SaveAsBinary(text);
                    if (text3 != null && text5 != null && text6 != null)
                    {
                        WriteAtomic(text3, text5);
                        string? pendingEditsPath = PendingEditsPath;
                        if (pendingEditsPath != null)
                        {
                            WriteAtomic(pendingEditsPath, text6);
                        }
                    }
                    return new StatsSaveResult(StatsSaveOutcome.SavedToFile);
                }
                catch (Exception ex)
                {
                    return new StatsSaveResult(StatsSaveOutcome.Failed, ex.Message);
                }
            }
        }
        return new StatsSaveResult(StatsSaveOutcome.Failed, "No Steam account found.");
    }

    private static string BuildCloudRedirect(string path, GameStatsEdit edit, Dictionary<string, AchievementInfo> achByName, uint now)
    {
        JsonObject jsonObject = (JsonNode.Parse(File.ReadAllText(path), null) as JsonObject) ?? throw new InvalidDataException("CloudRedirect's stats file couldn't be read.");
        JsonArray? jsonArray = jsonObject["achievements"] as JsonArray;
        if (jsonArray == null)
        {
            jsonArray = (JsonArray)(jsonObject["achievements"] = new JsonArray((JsonNodeOptions?)null));
        }
        foreach (KeyValuePair<string, bool> achievement in edit.Achievements)
        {
            achievement.Deconstruct(out var key, out var value);
            string key2 = key;
            bool flag = value;
            if (!achByName.TryGetValue(key2, out AchievementInfo? a))
            {
                continue;
            }
            ulong num = (ulong)(1L << a.Bit);
            JsonObject? jsonObject2 = jsonArray.OfType<JsonObject>().FirstOrDefault((JsonObject b) => Long(b["stat_id"]) == a.StatId);
            if (jsonObject2 == null)
            {
                if (!flag)
                {
                    continue;
                }
                jsonObject2 = new JsonObject((JsonNodeOptions?)null)
                {
                    ["stat_id"] = (long)a.StatId,
                    ["bits"] = 0L,
                    ["unlock_times"] = new JsonArray(Enumerable.Repeat(0L, 32).Select((Func<long, JsonNode>)((long z) => z)).ToArray())
                };
                jsonArray.Add(jsonObject2);
            }
            JsonArray? jsonArray2 = jsonObject2["unlock_times"] as JsonArray;
            if (jsonArray2 == null)
            {
                jsonArray2 = (JsonArray)(jsonObject2["unlock_times"] = new JsonArray((JsonNodeOptions?)null));
            }
            while (jsonArray2.Count < 32)
            {
                jsonArray2.Add(0L);
            }
            if (flag)
            {
                jsonObject2["bits"] = Long(jsonObject2["bits"]) | (long)num;
                if (Long(jsonArray2[a.Bit]) == 0L)
                {
                    jsonArray2[a.Bit] = (long)now;
                }
                continue;
            }
            jsonObject2["bits"] = Long(jsonObject2["bits"]) & (long)(~num);
            jsonArray2[a.Bit] = 0L;
            foreach (JsonObject item in (jsonObject["stats"] as JsonArray)?.OfType<JsonObject>() ?? Array.Empty<JsonObject>())
            {
                if (Long(item["id"]) == a.StatId)
                {
                    item["value"] = Long(item["value"]) & (long)(~num);
                }
            }
        }
        return jsonObject.ToJsonString();
    }

    private string BuildPendingEdits(uint acct, long appId, GameStatsEdit edit, Dictionary<string, AchievementInfo> achByName)
    {
        JsonObject jsonObject2;
        try
        {
            string? pendingEditsPath = PendingEditsPath;
            jsonObject2 = ((pendingEditsPath != null && File.Exists(pendingEditsPath) && JsonNode.Parse(File.ReadAllText(pendingEditsPath), null) is JsonObject jsonObject) ? jsonObject : new JsonObject((JsonNodeOptions?)null));
        }
        catch
        {
            jsonObject2 = new JsonObject((JsonNodeOptions?)null);
        }
        JsonObject? jsonObject3 = jsonObject2[acct.ToString()] as JsonObject;
        if (jsonObject3 == null)
        {
            jsonObject3 = (JsonObject)(jsonObject2[acct.ToString()] = new JsonObject((JsonNodeOptions?)null));
        }
        JsonArray? jsonArray = jsonObject3[appId.ToString()] as JsonArray;
        if (jsonArray == null)
        {
            jsonArray = (JsonArray)(jsonObject3[appId.ToString()] = new JsonArray((JsonNodeOptions?)null));
        }
        foreach (var (text2, flag2) in edit.Achievements)
        {
            if (!achByName.TryGetValue(text2, out AchievementInfo? value))
            {
                continue;
            }
            for (int num = jsonArray.Count - 1; num >= 0; num--)
            {
                if (jsonArray[num] is JsonObject jsonObject4 && jsonObject4["name"] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string? value2) && value2 == text2)
                {
                    jsonArray.RemoveAt(num);
                }
            }
            if (!flag2)
            {
                jsonArray.Add(new JsonObject((JsonNodeOptions?)null)
                {
                    ["name"] = text2,
                    ["stat"] = (long)value.StatId,
                    ["bit"] = (long)value.Bit
                });
            }
        }
        return jsonObject2.ToJsonString();
    }

    private static void WriteAtomic(string path, string text)
    {
        string dir = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string text2 = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(text2, text);
        File.Move(text2, path, overwrite: true);
    }

    private static long Long(JsonNode? node)
    {
        if (!(node is JsonValue jsonValue))
        {
            return 0L;
        }
        if (!jsonValue.TryGetValue<long>(out var value))
        {
            if (!jsonValue.TryGetValue<int>(out var value2))
            {
                if (!jsonValue.TryGetValue<double>(out var value3))
                {
                    return 0L;
                }
                return (long)value3;
            }
            return value2;
        }
        return value;
    }

    private static KeyValue? Child(KeyValue? kv, string name)
    {
        return kv?.Children?.FirstOrDefault((KeyValue c) => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static KeyValue Section(string name)
    {
        return new KeyValue
        {
            Name = name,
            Type = KeyValueType.None,
            Children = new List<KeyValue>(),
            Valid = true
        };
    }

    private static KeyValue Leaf(string name, KeyValueType type, object value)
    {
        return new KeyValue
        {
            Name = name,
            Type = type,
            Value = value,
            Valid = true
        };
    }

    private static KeyValue Add(KeyValue parent, KeyValue child)
    {
        (parent.Children ?? (parent.Children = new List<KeyValue>())).Add(child);
        return child;
    }

    private static KeyValue GetOrAdd(KeyValue parent, string name, Func<KeyValue> make)
    {
        return parent.Find(name) ?? Add(parent, make());
    }

    private static string Str(KeyValue? kv)
    {
        return kv?.Value?.ToString() ?? "";
    }

    private static int Int(KeyValue? kv)
    {
        object? obj = kv?.Value;
        if (!(obj is int result))
        {
            if (!(obj is uint result2))
            {
                if (!(obj is long num))
                {
                    if (!(obj is ulong num2))
                    {
                        if (!(obj is float num3))
                        {
                            if (obj is string s && int.TryParse(s, out var result3))
                            {
                                return result3;
                            }
                            return 0;
                        }
                        return (int)num3;
                    }
                    return (int)num2;
                }
                return (int)num;
            }
            return (int)result2;
        }
        return result;
    }

    private static double Num(KeyValue? kv)
    {
        object? obj = kv?.Value;
        if (!(obj is float num))
        {
            if (obj is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
            return Int(kv);
        }
        return num;
    }

    private static string Localized(KeyValue? node, string language, string fallback)
    {
        if (node == null)
        {
            return fallback;
        }
        string text = Str(Child(node, language));
        if (text.Length == 0 && language != "english")
        {
            text = Str(Child(node, "english"));
        }
        if (text.Length == 0 && node.Type == KeyValueType.String)
        {
            text = Str(node);
        }
        if (text.Length <= 0)
        {
            return fallback;
        }
        return text;
    }
}
