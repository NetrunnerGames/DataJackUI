using System.Collections.Generic;

namespace DataJackUIGui.Services.GameData;

public sealed record GameStatsSnapshot(long AppId, uint? AccountId, bool HasSchema, bool CloudRedirectManaged, IReadOnlyList<AchievementInfo> Achievements, IReadOnlyList<StatInfo> Stats, IReadOnlyList<AchievementGroup> Groups);
