using System.Text.Json;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using Xunit;

namespace DataJackUIGui.Tests;

public class RyuuManifestInfoTests
{
    private const string SampleJson = """
    {
      "app_id": "500",
      "available_branches": ["public"],
      "branch_manifests": {
        "public": {
          "depots": {
            "501": "1832333851347898090",
            "502": "1346199116149879810",
            "503": "5156732173895269546"
          },
          "files": [
            "500.lua",
            "501_1832333851347898090.manifest",
            "502_1346199116149879810.manifest",
            "503_5156732173895269546.manifest"
          ]
        }
      },
      "default_branch": "public",
      "depot_dlc_map": {},
      "depot_oslist_map": {
        "503": "Windows"
      },
      "dlc": [
        {
          "appid": 1660740,
          "has_depots": false,
          "in_lua": true,
          "name": "Left 4 Dead - Uncensored",
          "oslist": []
        }
      ],
      "local_branches": ["public"],
      "timestamp": "2026-10-16T00:56:03.418751",
      "workshop_depot": 0,
      "workshop_in_lua": false,
      "status": "ready"
    }
    """;

    [Fact]
    public void RyuuManifestInfo_DeserializesCorrectly()
    {
        var info = JsonSerializer.Deserialize<RyuuManifestInfo>(SampleJson);

        Assert.NotNull(info);
        Assert.Equal("500", info.AppId);
        Assert.True(info.IsAvailable);
        Assert.Equal(3, info.LatestSteamDepots.Count);
        Assert.Equal("1832333851347898090", info.LatestSteamDepots["501"]);
        Assert.Equal(4, info.DatabaseFiles.Count);
        Assert.Contains("500.lua", info.DatabaseFiles);
        Assert.NotNull(info.DepotOslistMap);
        Assert.Equal("Windows", info.DepotOslistMap["503"]);
        Assert.NotNull(info.LocalBranches);
        Assert.Contains("public", info.LocalBranches);
        Assert.False(info.WorkshopInLua);
        Assert.Single(info.Dlc!);
        Assert.Equal(1660740, info.Dlc![0].AppId);
        Assert.True(info.Dlc[0].InLua);
        Assert.NotNull(info.Dlc[0].OsList);
    }

    [Fact]
    public void RyuuManifestInfo_StatusWarming_ReportsUnavailable()
    {
        string warmingJson = """
        {
          "app_id": "99999",
          "status": "warming",
          "message": "Manifest info is warming."
        }
        """;

        var info = JsonSerializer.Deserialize<RyuuManifestInfo>(warmingJson);
        Assert.NotNull(info);
        Assert.False(info.IsAvailable);
    }

    [Fact]
    public void CheckGameUpdate_IdentifiesUpToDateGame_WithoutDownloading()
    {
        var info = JsonSerializer.Deserialize<RyuuManifestInfo>(SampleJson)!;

        // User has the exact manifests matching Steam's latest
        var installedDepots = new Dictionary<long, string>
        {
            [501] = "1832333851347898090",
            [502] = "1346199116149879810",
        };

        var outdated = new Dictionary<long, string>();
        bool ryuuHasAnyNew = false;
        var steamDepots = info.LatestSteamDepots;
        var ryuuFiles = info.DatabaseFiles;

        foreach (var (depotId, installedManifest) in installedDepots)
        {
            if (steamDepots.TryGetValue(depotId.ToString(), out string? latestSteamManifest))
            {
                if (!string.Equals(installedManifest, latestSteamManifest, StringComparison.OrdinalIgnoreCase))
                {
                    outdated[depotId] = latestSteamManifest;
                    string manifestFilename = $"{depotId}_{latestSteamManifest}.manifest";
                    if (ryuuFiles.Any(f => string.Equals(f, manifestFilename, StringComparison.OrdinalIgnoreCase)))
                    {
                        ryuuHasAnyNew = true;
                    }
                }
            }
        }

        Assert.Empty(outdated);
        Assert.False(ryuuHasAnyNew);
    }

    [Fact]
    public void CheckGameUpdate_IdentifiesOutdatedDepot_AndRyuuAvailability()
    {
        var info = JsonSerializer.Deserialize<RyuuManifestInfo>(SampleJson)!;

        // User has older manifest for depot 501
        var installedDepots = new Dictionary<long, string>
        {
            [501] = "old_manifest_111111",
            [502] = "1346199116149879810",
        };

        var outdated = new Dictionary<long, string>();
        bool ryuuHasAnyNew = false;
        var steamDepots = info.LatestSteamDepots;
        var ryuuFiles = info.DatabaseFiles;

        foreach (var (depotId, installedManifest) in installedDepots)
        {
            if (steamDepots.TryGetValue(depotId.ToString(), out string? latestSteamManifest))
            {
                if (!string.Equals(installedManifest, latestSteamManifest, StringComparison.OrdinalIgnoreCase))
                {
                    outdated[depotId] = latestSteamManifest;
                    string manifestFilename = $"{depotId}_{latestSteamManifest}.manifest";
                    if (ryuuFiles.Any(f => string.Equals(f, manifestFilename, StringComparison.OrdinalIgnoreCase)))
                    {
                        ryuuHasAnyNew = true;
                    }
                }
            }
        }

        Assert.Single(outdated);
        Assert.Equal("1832333851347898090", outdated[501]);
        Assert.True(ryuuHasAnyNew); // Ryuu has 501_1832333851347898090.manifest ready in DB!
    }
}
