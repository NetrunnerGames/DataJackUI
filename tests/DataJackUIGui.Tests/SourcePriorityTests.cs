using DataJackUIGui.Models;
using Xunit;

namespace DataJackUIGui.Tests;

public class SourcePriorityTests
{
    [Fact]
    public void SourcePriority_Matches_User_Specified_Hierarchy()
    {
        int depotBoxPriority = SourceMeta.GetPriority("DepotBox");
        int ryuuPriority = SourceMeta.GetPriority("Ryuu");
        int luiePriority = SourceMeta.GetPriority("Luie");
        int sushiPriority = SourceMeta.GetPriority("Sushi");
        int cloudPriority = SourceMeta.GetPriority("TwentyTwo Cloud");
        int sadiePriority = SourceMeta.GetPriority("Sadie (Morrenus)");

        // 1. DepotBox is #1 priority
        Assert.Equal(100, depotBoxPriority);

        // 2. Ryuu is #2 priority
        Assert.Equal(90, ryuuPriority);
        Assert.Equal(90, SourceMeta.GetPriority("Ryuu Direct"));
        Assert.True(depotBoxPriority > ryuuPriority, "DepotBox must have higher priority than Ryuu");

        // 3. Public sources are #3 priority
        Assert.Equal(50, luiePriority);
        Assert.Equal(50, sushiPriority);
        Assert.Equal(50, cloudPriority);
        Assert.True(ryuuPriority > luiePriority, "Ryuu must have higher priority than public sources");

        // 4. Keyed sources (Sadie) are lowest priority
        Assert.Equal(10, sadiePriority);
        Assert.True(luiePriority > sadiePriority, "Public sources must have higher priority than user-keyed sources");
    }

    [Fact]
    public void SourceOrdering_Sorts_Correctly_By_Priority()
    {
        var rawSources = new Dictionary<string, string>
        {
            ["Sadie (Morrenus)"] = "available",
            ["Luie"] = "available",
            ["Sushi"] = "available",
            ["Ryuu"] = "available",
            ["DepotBox"] = "available",
        };

        var sortedNames = rawSources
            .OrderByDescending(kv => SourceMeta.GetPriority(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        Assert.Equal("DepotBox", sortedNames[0]);
        Assert.Equal("Ryuu", sortedNames[1]);
        Assert.Contains(sortedNames[2], new[] { "Luie", "Sushi" });
        Assert.Contains(sortedNames[3], new[] { "Luie", "Sushi" });
        Assert.Equal("Sadie (Morrenus)", sortedNames[4]);
    }

    [Fact]
    public void FallbackChain_Sequentially_Selects_Next_Available_Source()
    {
        var availableSources = new List<string> { "DepotBox", "Ryuu", "Luie", "Sadie (Morrenus)" };
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pick 1: DepotBox
        var pick1 = availableSources
            .Where(s => !tried.Contains(s))
            .OrderByDescending(SourceMeta.GetPriority)
            .FirstOrDefault();
        Assert.Equal("DepotBox", pick1);
        tried.Add(pick1!);

        // Pick 2: Ryuu
        var pick2 = availableSources
            .Where(s => !tried.Contains(s))
            .OrderByDescending(SourceMeta.GetPriority)
            .FirstOrDefault();
        Assert.Equal("Ryuu", pick2);
        tried.Add(pick2!);

        // Pick 3: Luie
        var pick3 = availableSources
            .Where(s => !tried.Contains(s))
            .OrderByDescending(SourceMeta.GetPriority)
            .FirstOrDefault();
        Assert.Equal("Luie", pick3);
        tried.Add(pick3!);

        // Pick 4: Sadie
        var pick4 = availableSources
            .Where(s => !tried.Contains(s))
            .OrderByDescending(SourceMeta.GetPriority)
            .FirstOrDefault();
        Assert.Equal("Sadie (Morrenus)", pick4);
        tried.Add(pick4!);

        // Exhausted: null
        var pick5 = availableSources
            .Where(s => !tried.Contains(s))
            .OrderByDescending(SourceMeta.GetPriority)
            .FirstOrDefault();
        Assert.Null(pick5);
    }
}
