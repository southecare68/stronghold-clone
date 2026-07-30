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
        HardBotRunsTheSpyRing();
        BotShieldsAgainstTheAssassin();

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

    // A Hard bot also climbs the shared Spy Guild and looses a dagger at its rival —
    // once its own branch is in hand, it trains the spy that answers the rival's crown
    // and, funded by a fair tax, fires it. (Normal keeps to its war-tool; see AiSim.)
    static void HardBotRunsTheSpyRing()
    {
        var sim = new Simulation(TileMap.Skirmish(Skirmish.DefaultSize));
        Skirmish.Setup(sim, Skirmish.DefaultSize);
        sim.FogEnabled = false;
        sim.EnableAi(2, AiLevel.Hard, VictoryPath.Economic);   // trade gold funds the ring fastest
        sim.AddGold(1, 5000);                                   // a rival hoard worth skimming

        for (int t = 0; t < 60_000; t++)
        {
            sim.Tick(Array.Empty<Command>());
            if (t % 200 == 0 && sim.IsTechResearched(2, TechTree.Embezzler)
                && sim.SpyReadyAt(2, TechTree.Embezzler) > 0) break;
        }

        Console.WriteLine($"  spy ring    guild {(sim.IsTechResearched(2, TechTree.SpyGuild) ? "✓" : "✗")}   " +
                          $"embezzler {(sim.IsTechResearched(2, TechTree.Embezzler) ? "✓" : "✗")}   fired {(sim.SpyReadyAt(2, TechTree.Embezzler) > 0 ? "✓" : "✗")}");
        Check("Hard: the bot climbs the Spy Guild and trains the rival's dagger", sim.IsTechResearched(2, TechTree.Embezzler));
        Check("Hard: and looses it at the rival", sim.SpyReadyAt(2, TechTree.Embezzler) > 0);
    }

    // Defensive counter-tech: a bot whose rival has trained the Assassin rushes the
    // Bodyguard — the one counter it does not already pick up climbing its own branch.
    static void BotShieldsAgainstTheAssassin()
    {
        var sim = new Simulation(TileMap.Skirmish(Skirmish.DefaultSize));
        Skirmish.Setup(sim, Skirmish.DefaultSize);
        sim.FogEnabled = false;
        // The rival (owner 1) trains the Assassin; the bot (owner 2) should react.
        sim.AddResearch(1, 1000);
        foreach (int id in new[] { TechTree.Roads, TechTree.Muster, TechTree.SpyGuild, TechTree.Assassin }) sim.TryResearch(1, id);
        sim.EnableAi(2, AiLevel.Normal, VictoryPath.Religious);

        int finish = -1;
        for (int t = 0; t < 40_000; t++)
        {
            sim.Tick(Array.Empty<Command>());
            if (t % 200 == 0 && sim.IsTechResearched(2, TechTree.Bodyguard)) { finish = t; break; }
        }
        Console.WriteLine($"  defence     rival has Assassin ✓   bot Bodyguard {(sim.IsTechResearched(2, TechTree.Bodyguard) ? $"✓ @ {finish}" : "✗")}");
        Check("a threatened bot rushes the Bodyguard", sim.IsTechResearched(2, TechTree.Bodyguard));
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
