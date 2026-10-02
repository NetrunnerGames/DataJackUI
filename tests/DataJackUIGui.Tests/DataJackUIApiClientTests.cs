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

    // ── ParseGameFixListings: real DepotBox format ──────────────────

    [Fact]
    public void ParseGameFixListings_RealDepotBoxFormat_ParsesGamesFixesAndTags()
    {
        // This is the actual shape DepotBox returns
        string json = """
        {
          "success": true,
          "tags": ["bypass","online","hypervisor"],
          "availableTags": ["online","bypass","hypervisor"],
          "count": 2,
          "games": [
            {
              "appid": "730",
              "name": "Counter-Strike 2",
              "headerImage": "https://example.com/header.jpg",
              "capsuleImage": "https://example.com/capsule.jpg",
              "fixes": [
                {
                  "id": "abc123",
                  "downloadName": "Counter_Strike_2_bypass.zip",
                  "filename": "Counter_Strike_2_bypass.zip",
                  "size": "5.5 MB",
                  "badges": ["Bypass"],
                  "tags": ["bypass"]
                },
                {
                  "id": "def456",
                  "downloadName": "Counter_Strike_2_online.zip",
                  "filename": "Counter_Strike_2_online.zip",
                  "size": "12.3 MB",
                  "badges": ["Online"],
                  "tags": ["online"]
                }
              ]
            },
            {
              "appid": "570",
              "name": "Dota 2",
              "headerImage": "https://example.com/dota2.jpg",
              "fixes": [
                {
                  "id": "ghi789",
                  "downloadName": "Dota_2_online.zip",
                  "filename": "Dota_2_online.zip",
                  "size": "10 MB",
                  "badges": ["Online"],
                  "tags": ["online"]
                }
              ]
            }
          ]
        }
        """;

        var result = DataJackUIApiClient.ParseGameFixListings(json);

        Assert.NotNull(result);
        Assert.Equal(2, result.Games.Count);

        // CS2: 2 fixes, tags aggregated from fixes
        var cs = result.Games[0];
        Assert.Equal("730", cs.AppId);
        Assert.Equal("Counter-Strike 2", cs.Name);
        Assert.Equal("https://example.com/header.jpg", cs.HeaderImage);
        Assert.Equal(2, cs.FixCount);
        Assert.Equal(2, cs.Fixes.Count);
        Assert.Equal(2, cs.Tags.Count);
        Assert.Contains(cs.Tags, t => t.Id == "bypass");
        Assert.Contains(cs.Tags, t => t.Id == "online");

        // Dota 2: 1 fix
        var dota = result.Games[1];
        Assert.Equal("570", dota.AppId);
        Assert.Equal("Dota 2", dota.Name);
        Assert.Equal(1, dota.FixCount);
        Assert.Single(dota.Tags);
        Assert.Equal("online", dota.Tags[0].Id);
    }

    [Fact]
    public void ParseGameFixListings_ArrayPayload_ParsesCorrectly()
    {
        // Fallback: some endpoints might return a plain array
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
        Assert.Single(game1.Tags);
        Assert.Equal("bypass", game1.Tags[0].Id);

        var game2 = result.Games[1];
        Assert.Equal("67890", game2.AppId);
        Assert.Equal("Cyberpunk 2077", game2.Name);
        Assert.Single(game2.Tags);
        Assert.Equal("online", game2.Tags[0].Id);
    }

    [Fact]
    public void ParseGameFixListings_InfersTagsFromFixesWhenGameHasNoTags()
    {
        string json = """
        {
          "success": true,
          "count": 1,
          "games": [
            {
              "appid": "1001",
              "name": "Grand Theft Auto V",
              "fixes": [
                {
                  "id": "fix1",
                  "downloadName": "GTA_V_online.zip",
                  "tags": ["online"]
                }
              ]
            }
          ]
        }
        """;

        var result = DataJackUIApiClient.ParseGameFixListings(json);

        Assert.NotNull(result);
        var gta = result.Games[0];
        Assert.Single(gta.Tags);
        Assert.Equal("online", gta.Tags[0].Id);
        Assert.Equal(1, gta.FixCount);
    }

    [Fact]
    public void ParseGameFixListings_InfersTagsFromNameWhenNoFixesOrTags()
    {
        string json = """
        [
          {
            "id": "1002",
            "name": "Denuvo OwO Hypervisor"
          }
        ]
        """;

        var result = DataJackUIApiClient.ParseGameFixListings(json);

        Assert.NotNull(result);
        var denuvo = result.Games[0];
        Assert.Single(denuvo.Tags);
        Assert.Equal("hypervisor", denuvo.Tags[0].Id);
    }

    // ── GetGameFixListingsAsync: sends correct API key header ────────

    [Fact]
    public async Task GetGameFixListingsAsync_SendsApiKeyHeaderAndParsesResponse()
    {
        string json = """
        {
          "success": true,
          "count": 1,
          "games": [
            {
              "appid": "730",
              "name": "Counter-Strike 2",
              "headerImage": "https://example.com/cs2.jpg",
              "fixes": [
                {
                  "id": "abc123",
                  "downloadName": "CS2_bypass.zip",
                  "tags": ["bypass"]
                }
              ]
            }
          ]
        }
        """;

        StubHttpHandler? stub = null;
        stub = new StubHttpHandler(req =>
        {
            Assert.True(req.Headers.Contains("X-API-Key"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });

        using var client = new HttpClient(stub);
        var settings = new SettingsService();
        var api = new DataJackUIApiClient(client, null!, null!, new SteamAppListCache(), null!, settings);

        var listings = await api.GetGameFixListingsAsync();

        Assert.NotNull(listings);
        Assert.Single(listings.Games);

        var game = listings.Games[0];
        Assert.Equal("730", game.AppId);
        Assert.Equal("Counter-Strike 2", game.Name);
        Assert.Equal(1, game.FixCount);
        Assert.Single(game.Tags);
        Assert.Equal("bypass", game.Tags[0].Id);
    }

    // ── ParseGameFixes: real DepotBox ?q= format (games wrapper) ────

    [Fact]
    public void ParseGameFixes_RealDepotBoxFormat_ParsesFixesFromGamesWrapper()
    {
        // The ?q= endpoint returns the exact same { success, games: [...] } wrapper
        string json = """
        {
          "success": true,
          "tags": ["online","bypass","hypervisor"],
          "count": 1,
          "games": [
            {
              "appid": "730",
              "name": "Counter-Strike 2",
              "headerImage": "https://example.com/cs2.jpg",
              "capsuleImage": "https://example.com/capsule.jpg",
              "fixes": [
                {
                  "id": "abc123",
                  "downloadName": "CS2_bypass.zip",
                  "filename": "CS2_bypass.zip",
                  "size": "5.5 MB",
                  "badges": ["Bypass", "Tested"],
                  "tags": ["bypass"]
                }
              ]
            }
          ]
        }
        """;

        var result = DataJackUIApiClient.ParseGameFixes(json, "730");

        Assert.NotNull(result);
        Assert.Equal("730", result.AppId);
        Assert.Equal("Counter-Strike 2", result.Name);
        Assert.Equal("https://example.com/cs2.jpg", result.HeaderImage);
        Assert.Single(result.Fixes);

        var fix = result.Fixes[0];
        Assert.Equal("abc123", fix.Id);
        Assert.Equal("CS2_bypass.zip", fix.FixFilename);
        Assert.True(fix.HasFix);
        Assert.Single(fix.Tags);
        Assert.Equal("bypass", fix.Tags[0].Id);
        Assert.Contains("5.5 MB", fix.Description);
    }

    [Fact]
    public void ParseGameFixes_FallbackDirectFormat_ParsesFixes()
    {
        // Fallback: direct { name, fixes: [...] } without a games wrapper
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
    public void ParseGameFixes_MultipleGames_MatchesCorrectAppId()
    {
        // ?q= can return multiple games; we should pick the one matching appid
        string json = """
        {
          "success": true,
          "count": 2,
          "games": [
            {
              "appid": "999",
              "name": "Wrong Game",
              "fixes": [{ "id": "x", "tags": ["online"] }]
            },
            {
              "appid": "730",
              "name": "Counter-Strike 2",
              "fixes": [{ "id": "correct", "tags": ["bypass"] }]
            }
          ]
        }
        """;

        var result = DataJackUIApiClient.ParseGameFixes(json, "730");

        Assert.NotNull(result);
        Assert.Equal("730", result.AppId);
        Assert.Equal("Counter-Strike 2", result.Name);
        Assert.Single(result.Fixes);
        Assert.Equal("correct", result.Fixes[0].Id);
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

    // ── ParseGameFixElement: DepotBox fix element fields ────────────

    [Fact]
    public void ParseGameFixListings_FixElement_ParsesDownloadNameAndSize()
    {
        string json = """
        {
          "success": true,
          "count": 1,
          "games": [
            {
              "appid": "752590",
              "name": "A Plague Tale: Innocence",
              "fixes": [
                {
                  "id": "55f13d642066cd46",
                  "downloadName": "A_Plague_Tale_Innocence_bypass.zip",
                  "filename": "A_Plague_Tale_Innocence_bypass.zip",
                  "size": "5.5 MB",
                  "badges": ["Bypass", "Tested"],
                  "tags": ["bypass"]
                }
              ]
            }
          ]
        }
        """;

        var result = DataJackUIApiClient.ParseGameFixListings(json);

        Assert.NotNull(result);
        var game = result.Games[0];
        Assert.Equal("752590", game.AppId);
        Assert.Equal(1, game.FixCount);
        Assert.Single(game.Fixes);

        var fix = game.Fixes[0];
        Assert.Equal("55f13d642066cd46", fix.Id);
        Assert.Equal("A_Plague_Tale_Innocence_bypass.zip", fix.FixFilename);
        Assert.Contains("5.5 MB", fix.Description);
    }
}
