// AiPaths — the bot contests any crown, not just the Religious one.
//
// A path-aware bot climbs its assigned branch to the capstone (which unlocks that
// crown) and raises the structures the crown wants — churches, wonders, new keeps —
// while its metric (faith, wonders, gold, population) climbs. Here each of the four
// paths is handed to a Hard bot on a skirmish map against a passive rival, and we
// confirm it reaches the capstone and makes real progress on the metric.
//
// Determinism and the difficulty gradient are AiSim's job; this is the liveness of
// the four pursuits. Run with `dotnet run`.

using System;
using Sim;

static class Program
{
    const int Cap = 45_000;   // enough for the slower crowns; most finish well before this
    static int _failures;

    static void Main()
    {
        Console.WriteLine("AiPaths — the bot contests each crown\n");

        Contest(VictoryPath.Religious, TechTree.GrandTemple);
        Contest(VictoryPath.Economic, TechTree.GrandExchange);
        Contest(VictoryPath.Science, TechTree.Academy);
        Contest(VictoryPath.Domain, TechTree.SovereignsCourt);

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    static void Contest(VictoryPath path, int capstone)
    {
        var sim = new Simulation(TileMap.Skirmish(Skirmish.DefaultSize));
        Skirmish.Setup(sim, Skirmish.DefaultSize);
        sim.FogEnabled = false;                       // let the bot build freely, so we test the PURSUIT, not scouting
        sim.EnableAi(2, AiLevel.Hard, path);          // owner 1 stays passive

        int finish = -1;
        for (int t = 0; t < Cap; t++)
        {
            sim.Tick(Array.Empty<Command>());
            if (t % 200 == 0 && sim.IsTechResearched(2, capstone) && Progressed(sim, path)) { finish = t; break; }
        }

        bool reached = sim.IsTechResearched(2, capstone);
        Console.WriteLine($"  {path,-10}  capstone {(reached ? "✓" : "✗")}   {(finish < 0 ? "(cap)" : $"@ {finish}"),8}   {Detail(sim, path)}");

        Check($"{path}: the bot reaches its capstone", reached);
        Check($"{path}: and drives the crown's metric", Progressed(sim, path));
    }

    // Real progress on the crown's own metric — not merely the capstone research.
    static bool Progressed(Simulation sim, VictoryPath path) => path switch
    {
        VictoryPath.Religious => sim.CountBuildings(2, BuildingType.Church) > 0 && sim.Faith(2) > 25,
        VictoryPath.Economic  => sim.EconomicIncome(2) > 0 && sim.Gold(2) > 1000,
        VictoryPath.Science   => sim.WonderCount(2) >= 1,
        _                     => sim.TerritoryCount(2) >= 2 || sim.PeasantCount(2) >= 40,   // founded land or grew a census
    };

    static string Detail(Simulation sim, VictoryPath path) => path switch
    {
        VictoryPath.Religious => $"{sim.CountBuildings(2, BuildingType.Church)} churches, faith {sim.Faith(2)}%",
        VictoryPath.Economic  => $"gold {sim.Gold(2)}, +{sim.EconomicIncome(2)}/turn",
        VictoryPath.Science   => $"{sim.WonderCount(2)} wonders",
        _                     => $"{sim.PeasantCount(2)} pop, {sim.TerritoryCount(2)} territories",
    };

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "  ok" : "FAIL")}  {what}");
        if (!ok) _failures++;
    }
}
