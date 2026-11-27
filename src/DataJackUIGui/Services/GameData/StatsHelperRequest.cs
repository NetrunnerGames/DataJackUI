using System.Collections.Generic;

namespace DataJackUIGui.Services.GameData;

internal sealed record StatsHelperRequest(Dictionary<string, bool> Achievements, Dictionary<string, double> Stats);
