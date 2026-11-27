namespace DataJackUIGui.Services.GameData;

internal sealed record StatsHelperResponse(bool Ok, string? Error = null, int? SteamResult = null);
