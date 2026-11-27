using System;

namespace DataJackUIGui.Services.GameData;

public sealed record AchievementInfo(
    string ApiName,
    int StatId,
    int Bit,
    string Name,
    string Description,
    bool Hidden,
    bool Protected,
    string? IconUrl,
    string? IconGrayUrl,
    bool Unlocked,
    DateTimeOffset? UnlockedAt,
    int? GroupId = null,
    AchievementProgress? Progress = null)
{
    public AchievementProgress? Progress { get; set; } = Progress;
}
