namespace DataJackUIGui.Services.GameData;

public sealed record StatInfo(int StatId, string ApiName, string Name, bool IsFloat, double Value, bool Protected, bool IncrementOnly);
