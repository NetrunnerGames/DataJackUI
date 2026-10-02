using System.Net;
using System.Net.Http;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using Xunit;

namespace DataJackUIGui.Tests;

public class DataJackUIApiClientTests
{
    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(reply(request));
        }
    }

    [Fact]
    public void ParseGameFixListings_ObjectPayload_ParsesGamesAndTagsCorrectly()
    {
        string json = """
        {
          "games": [
            {
              "appid": "730",
              "name": "Counter-Strike 2",
              "fixCount": 2,
              "tags": [{"id": "bypass", "name": "Bypass"}]
            },
            {
              "appid": "570",
              "name": "Dota 2",
              "fix_count": 3,
              "tags": [{"id": "online", "name": "Online"}]
            }
          ]
        }
        """;

        var result = DataJackUIApiClient.ParseGameFixListings(json);

        Assert.NotNull(result);
        Assert.Equal(2, result.Games.Count);

        var cs = result.Games[0];
        Assert.Equal("730", cs.AppId);
        Assert.Equal("Counter-Strike 2", cs.Name);
        Assert.Equal(2, cs.FixCount);
        Assert.Single(cs.Tags);
        Assert.Equal("bypass", cs.Tags[0].Id);

        var dota = result.Games[1];
        Assert.Equal("570", dota.AppId);
        Assert.Equal("Dota 2", dota.Name);
        Assert.Equal(3, dota.FixCount);
    }

    [Fact]
    public void ParseGameFixListings_ArrayPayload_ParsesGamesCorrectly()
    {
        string json = """
        [
          {
            "app_id": "12345",
            "title": "007 First Light",
            "tag": "bypass",
            "file": "007_First_Light_bypass.zip"
          },
          {
            "id": "67890",
            "game_name": "Cyberpunk 2077",
            "tags": ["online"]
          }
        ]
        """;

        var result = DataJackUIApiClient.ParseGameFixListings(json);

        Assert.NotNull(result);
        Assert.Equal(2, result.Games.Count);

        var game1 = result.Games[0];
        Assert.Equal("12345", game1.AppId);
        Assert.Equal("007 First Light", game1.Name);
        Assert.Equal(1, game1.FixCount);
        Assert.Single(game1.Tags);
        Assert.Equal("bypass", game1.Tags[0].Id);

        var game2 = result.Games[1];
        Assert.Equal("67890", game2.AppId);
        Assert.Equal("Cyberpunk 2077", game2.Name);
        Assert.Equal(1, game2.FixCount);
        Assert.Single(game2.Tags);
        Assert.Equal("online", game2.Tags[0].Id);
    }

    [Fact]
    public void ParseGameFixListings_InfersTagsWhenMissing()
    {
        string json = """
        [
          {
            "id": "1001",
            "name": "Grand Theft Auto V Online Fix"
          },
          {
            "id": "1002",
            "name": "Denuvo OwO Hypervisor"
          }
        ]
        """;

        var result = DataJackUIApiClient.ParseGameFixListings(json);

        Assert.NotNull(result);
        Assert.Equal(2, result.Games.Count);

        var gta = result.Games[0];
        Assert.Single(gta.Tags);
        Assert.Equal("online", gta.Tags[0].Id);

        var denuvo = result.Games[1];
        Assert.Single(denuvo.Tags);
        Assert.Equal("hypervisor", denuvo.Tags[0].Id);
    }

    [Fact]
    public async Task GetGameFixListingsAsync_GroupsDuplicatesAndSumsFixCounts()
    {
        string json = """
        [
          {
            "appid": "730",
            "name": "Counter-Strike 2",
            "tag": "bypass",
            "fix_count": 1
          },
          {
            "appid": "730",
            "name": "Counter-Strike 2",
            "tag": "online",
            "fix_count": 2
          }
        ]
        """;

        var stub = new StubHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        using var client = new HttpClient(stub);
        var settings = new SettingsService();
        var api = new DataJackUIApiClient(client, null!, null!, new SteamAppListCache(), null!, settings);

        var listings = await api.GetGameFixListingsAsync();

        Assert.NotNull(listings);
        Assert.Single(listings.Games);

        var game = listings.Games[0];
        Assert.Equal("730", game.AppId);
        Assert.Equal(3, game.FixCount); // 1 + 2 = 3
        Assert.Equal(2, game.Tags.Count); // bypass + online
    }

    [Fact]
    public void ParseGameFixes_ObjectPayload_ParsesFixesDetail()
    {
        string json = """
        {
          "appid": "730",
          "name": "Counter-Strike 2",
          "fixes": [
            {
              "id": "cs2_bypass",
              "title": "CS2 Bypass Fix",
              "hasManifest": true,
              "hasFix": true,
              "tag": "bypass"
            }
          ]
        }
        """;

        var result = DataJackUIApiClient.ParseGameFixes(json, "730");

        Assert.NotNull(result);
        Assert.Equal("730", result.AppId);
        Assert.Equal("Counter-Strike 2", result.Name);
        Assert.Single(result.Fixes);

        var fix = result.Fixes[0];
        Assert.Equal("cs2_bypass", fix.Id);
        Assert.Equal("CS2 Bypass Fix", fix.Title);
        Assert.True(fix.HasManifest);
        Assert.True(fix.HasFix);
        Assert.Single(fix.Tags);
        Assert.Equal("bypass", fix.Tags[0].Id);
    }

    [Fact]
    public async Task GetGameFixesAsync_WhenEmptyOrError_ReturnsFallbackResponse()
    {
        var stub = new StubHttpHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(stub);
        var settings = new SettingsService();
        var api = new DataJackUIApiClient(client, null!, null!, new SteamAppListCache(), null!, settings);

        var res = await api.GetGameFixesAsync("99999");

        Assert.NotNull(res);
        Assert.Equal("99999", res.AppId);
        Assert.Single(res.Fixes);
        Assert.Equal("99999_bypass", res.Fixes[0].Id);
    }
}
