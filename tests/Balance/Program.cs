// Balance — a path-race harness.
//
// Each of the four crowns is pursued by a lone realm on the SAME granted economy:
// every realm tick it is handed a fixed income of build resources, it researches
// the next node on its branch, it raises its path's structures, and its metric
// (gold / faith / population / wonders) evolves by the real rules. We measure the
// tick at which the HIGH goal is first met — the "reach" — and compare the four, so
// the constants can be tuned until no path trivially out-races the others.
//
// Gold is NOT granted (Economic must earn it through trade); population grows only
// by migration (Domain must settle it). Everything else — a mature stockpile — is
// handed out equally, so what we measure is the PATH, not the economy.
//
// Run with `dotnet run`.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    const int Realm = 40;              // ticks per realm step
    const int Cap = 300_000;           // ~4 hours of game time — long enough to expose a slow path
    static int _failures;

    static readonly Dictionary<VictoryPath, int[]> Plans = new()
    {
        [VictoryPath.Economic]  = new[] { TechTree.Roads, TechTree.Market, TechTree.TradePost, TechTree.Monopoly, TechTree.BankingHouse, TechTree.GrandExchange },
        [VictoryPath.Religious] = new[] { TechTree.Roads, TechTree.Chapel, TechTree.Shrine, TechTree.Missionaries, TechTree.Cathedral, TechTree.GrandTemple },
        [VictoryPath.Science]   = new[] { TechTree.Roads, TechTree.ScholarsHut, TechTree.Library, TechTree.Scholarship, TechTree.PrintingPress, TechTree.Academy },
        [VictoryPath.Domain]    = new[] { TechTree.Roads, TechTree.Farmstead, TechTree.Husbandry, TechTree.Homesteads, TechTree.ProvincialKeeps, TechTree.SovereignsCourt },
    };

    static void Main()
    {
        Console.WriteLine("Balance — path race to each crown\n");
        Console.WriteLine($"  {"path",-10} {"reach",8}  {"minutes",8}   detail");

        var reach = new Dictionary<VictoryPath, int>();
        foreach (VictoryPath p in new[] { VictoryPath.Economic, VictoryPath.Religious, VictoryPath.Science, VictoryPath.Domain })
        {
            var (t, detail) = Pursue(p);
            reach[p] = t;
            string mins = t < 0 ? "  —  " : $"{t / 20.0 / 60.0,6:0.0}";
            Console.WriteLine($"  {p,-10} {(t < 0 ? "over" : t.ToString()),8}  {mins,8}   {detail}");
        }

        Console.WriteLine();
        int min = int.MaxValue, max = 0;
        foreach (var kv in reach) { if (kv.Value < 0) { max = Cap; } else { min = Math.Min(min, kv.Value); max = Math.Max(max, kv.Value); } }
        bool allReached = true;
        foreach (var kv in reach) if (kv.Value < 0) allReached = false;
        double spread = min > 0 ? (double)max / min : 0;

        Check("every path reaches its crown within the cap", allReached);
        Check($"the spread stays within 8x (fastest {min}, slowest {max}, {spread:0.0}x)", allReached && spread <= 8.0);

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    static (int reach, string detail) Pursue(VictoryPath path)
    {
        var sim = new Simulation(TileMap.Open(96));
        sim.PlaceBuilding(BuildingType.Keep, 1, 8, 8);
        sim.SetPopularity(1, 100);
        sim.AddResource(1, ResourceType.Food, 500_000);      // never starve over a long race
        for (int i = 0; i < 10; i++) sim.SpawnPeasant(1);    // a starting workforce
        int placed = 0;                                       // building-spot cursor

        for (int t = 1; t <= Cap; t++)
        {
            if (t % Realm == 0)
            {
                // A working economy's build income — modest, so a stone-hungry path
                // (wonders, keeps) genuinely takes time (NOT gold, NOT population).
                sim.AddResource(1, ResourceType.Wood, 12);
                sim.AddResource(1, ResourceType.Stone, 8);
                sim.AddResource(1, ResourceType.Food, 18);
                ResearchNext(sim, Plans[path]);
                PursueBuild(sim, path, ref placed);
            }
            sim.Tick(NoCmd);
            if (t % Realm == 0 && sim.Progress(1, path).HighMet) return (t, Detail(sim, path));
        }
        return (-1, Detail(sim, path));
    }

    static void ResearchNext(Simulation sim, int[] plan)
    {
        foreach (int id in plan) if (sim.TryResearch(1, id)) return;   // one node per realm tick, in order
    }

    static void PursueBuild(Simulation sim, VictoryPath path, ref int placed)
    {
        switch (path)
        {
            case VictoryPath.Economic:
                break;   // the branch's trade income is the whole engine — nothing to build

            case VictoryPath.Religious:
                // Keep churches ahead of the (small) flock so faith climbs to 75%.
                if (sim.Faith(1) < 75 && sim.CountBuildings(1, BuildingType.Church) < 4)
                    TryBuild(sim, BuildingType.Church, ChurchSpot(placed++));
                break;

            case VictoryPath.Science:
                // Once the Academy stands, raise the two wonders as stone allows — by
                // wonders STANDING (they take time to finish), not just those counting.
                if (sim.IsTechResearched(1, TechTree.Academy) && sim.CountBuildings(1, BuildingType.Wonder) < 2)
                    TryBuild(sim, BuildingType.Wonder, WonderSpot(sim.CountBuildings(1, BuildingType.Wonder)));
                break;

            case VictoryPath.Domain:
                // Houses for the room to grow (migration fills them), then found keeps.
                if (sim.PopulationCap(1) < 400 && sim.CountBuildings(1, BuildingType.House) < 12)
                    TryBuild(sim, BuildingType.House, HouseSpot(placed++));
                if (sim.IsTechResearched(1, TechTree.ProvincialKeeps) && sim.TerritoryCount(1) < 5)
                    TryBuild(sim, BuildingType.Keep, KeepSpot(sim.TerritoryCount(1)));
                break;
        }
    }

    static void TryBuild(Simulation sim, BuildingType t, (int x, int y) at)
        => sim.Tick(new[] { new Command { Owner = 1, Type = CommandType.Build, TargetId = (int)t, X = at.x, Y = at.y } });

    // Building spots on the open map, spaced so footprints never collide.
    static (int, int) ChurchSpot(int i) => (24 + (i % 10) * 3, 24 + (i / 10) * 3);
    static (int, int) HouseSpot(int i)  => (24 + (i % 12) * 4, 60 + (i / 12) * 4);
    static (int, int) WonderSpot(int i) => (70 + i * 5, 24);
    static readonly (int, int)[] KeepSites = { (40, 8), (72, 8), (8, 40), (40, 40) };
    static (int, int) KeepSpot(int owned) => KeepSites[Math.Min(owned - 1, KeepSites.Length - 1)];

    static string Detail(Simulation sim, VictoryPath path) => path switch
    {
        VictoryPath.Economic  => $"gold {sim.Gold(1)}",
        VictoryPath.Religious => $"faith {sim.Faith(1)}%, {sim.CountBuildings(1, BuildingType.Church)} churches",
        VictoryPath.Science   => $"{sim.WonderCount(1)} wonders",
        _                     => $"{sim.PeasantCount(1)} pop, {sim.TerritoryCount(1)} territories",
    };

    static readonly Command[] NoCmd = Array.Empty<Command>();

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
        if (!ok) _failures++;
    }
}
