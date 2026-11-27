using System.Collections.Generic;

namespace DataJackUIGui.Services.GameData;

public sealed record GameStatsEdit(IReadOnlyDictionary<string, bool> Achievements, IReadOnlyDictionary<string, double> Stats);
