using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using SAM.API;
using SAM.API.Callbacks;
using SAM.API.Types;

namespace DataJackUIGui.Services.GameData;

internal static class StatsHelper
{
    private const int SteamOk = 1;

    internal static bool IsFetchOnly(StatsHelperRequest req)
    {
        if (req.Achievements.Count == 0)
        {
            return req.Stats.Count == 0;
        }
        return false;
    }

    public static int Run(long appId)
    {
        try
        {
            StatsHelperRequest req = JsonSerializer.Deserialize<StatsHelperRequest>(Console.In.ReadLine() ?? "") ?? throw new InvalidDataException("empty request");
            StatsHelperResponse statsHelperResponse = Apply(appId, req);
            Console.Out.WriteLine(JsonSerializer.Serialize(statsHelperResponse));
            return (!statsHelperResponse.Ok) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new StatsHelperResponse(Ok: false, ex.Message, null)));
            return 1;
        }
    }

    private static StatsHelperResponse Apply(long appId, StatsHelperRequest req)
    {
        using Client client = new Client();
        try
        {
            client.Initialize(appId);
        }
        catch (Exception ex)
        {
            return new StatsHelperResponse(Ok: false, "Steam refused to open this game: " + ex.Message, null);
        }
        int? received = null;
        int? stored = null;
        client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsReceived>().OnRun += delegate (SAM.API.Types.UserStatsReceived p)
        {
            received = p.Result;
        };
        client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsStored>().OnRun += delegate (SAM.API.Types.UserStatsStored p)
        {
            stored = p.Result;
        };
        if (client.SteamUser == null || client.SteamUserStats == null)
        {
            return new StatsHelperResponse(Ok: false, "Steam interfaces were not initialized.", null);
        }
        var steamUser = client.SteamUser;
        var steamUserStats = client.SteamUserStats;
        steamUserStats.RequestUserStats(steamUser.GetSteamId());
        if (!Pump(client, () => received.HasValue) || received != 1)
        {
            return new StatsHelperResponse(Ok: false, "Steam didn't load this game's stats.", received);
        }
        if (IsFetchOnly(req))
        {
            return new StatsHelperResponse(Ok: true, null, received);
        }
        List<string> list = new List<string>();
        string key;
        foreach (KeyValuePair<string, bool> achievement in req.Achievements)
        {
            achievement.Deconstruct(out key, out var value);
            string text = key;
            bool state = value;
            if (!steamUserStats.SetAchievement(text, state))
            {
                list.Add(text);
            }
        }
        foreach (KeyValuePair<string, double> stat in req.Stats)
        {
            stat.Deconstruct(out key, out var value2);
            string text2 = key;
            double num = value2;
            if (!(steamUserStats.GetStatValue(text2, out int _) ? steamUserStats.SetStatValue(text2, (int)Math.Round(num)) : steamUserStats.SetStatValue(text2, (float)num)))
            {
                list.Add(text2);
            }
        }
        if (!steamUserStats.StoreStats() || !Pump(client, () => stored.HasValue))
        {
            return new StatsHelperResponse(Ok: false, "Steam didn't store the change.", stored);
        }
        if (stored != 1)
        {
            return new StatsHelperResponse(Ok: false, $"Steam rejected the change (result {stored}).", stored);
        }
        return (list.Count == 0) ? new StatsHelperResponse(Ok: true, null, stored) : new StatsHelperResponse(Ok: false, "Steam refused: " + string.Join(", ", list), stored);
    }

    private static bool Pump(Client client, Func<bool> done)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10.0))
        {
            client.RunCallbacks(server: false);
            if (done())
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return false;
    }
}
