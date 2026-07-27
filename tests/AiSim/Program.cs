// AiSim — run a bot-vs-bot skirmish and check two things:
//   1. Determinism: two independent sims stepped identically stay bit-for-bit
//      equal at every tick, and a fresh re-run reproduces the same final state.
//      The AI runs inside the tick and touches shared state, so this is the guard
//      that it cannot silently desync a networked match.
//   2. Liveness: the bot actually plays — it raises buildings, arms an army, and
//      marches it into a fight. A bot that does nothing would "pass" a pure
//      determinism check, so we assert it is alive too.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    const int Ticks = 2000;   // long enough to build an economy, train, and march across to clash
    static readonly List<Command> None = new();

    static Simulation FreshMatch()
    {
        var sim = new Simulation(TileMap.Skirmish(Skirmish.DefaultSize));
        Skirmish.Setup(sim, Skirmish.DefaultSize);
        sim.EnableAi(1);
        sim.EnableAi(2);
        return sim;
    }

    static int Main()
    {
        var a = FreshMatch();
        var b = FreshMatch();

        int firstDivergeTick = -1;
        int peakUnits = a.Units.Count, peakArmy1 = 0, peakArmy2 = 0;
        for (int t = 0; t < Ticks; t++)
        {
            a.Tick(None);
            b.Tick(None);
            if (firstDivergeTick < 0 && a.StateChecksum() != b.StateChecksum())
                firstDivergeTick = t;
            peakUnits = Math.Max(peakUnits, a.Units.Count);
            peakArmy1 = Math.Max(peakArmy1, a.ArmySize(1));
            peakArmy2 = Math.Max(peakArmy2, a.ArmySize(2));
            if (Environment.GetEnvironmentVariable("AIDBG") == "1" && t % 200 == 199)
            {
                int soldiers = 0, minX = 9999, maxX = -1, targeting = 0;
                foreach (var u in a.Units)
                    if (u.Owner == 2 && u.Alive && !u.IsPeasant)
                    {
                        soldiers++;
                        int tx = u.X >> 16; if (tx < minX) minX = tx; if (tx > maxX) maxX = tx;
                        if (u.TargetId != 0 || u.TargetBuildingId != 0) targeting++;
                    }
                Console.WriteLine($"t{t+1}: peas={a.PeasantCount(2)} idle={a.IdlePeasantCount(2)} army={a.ArmySize(2)} " +
                    $"food={a.Stockpile(2,ResourceType.Food)} wood={a.Stockpile(2,ResourceType.Wood)} " +
                    $"P2 soldiers x∈[{minX},{maxX}] targeting={targeting}  (enemy keep x≈{Skirmish.West(Skirmish.DefaultSize)})");
            }
        }

        bool inSync = firstDivergeTick < 0;

        // Reproducibility: a brand-new match, same seed, same final state.
        var c = FreshMatch();
        for (int t = 0; t < Ticks; t++) c.Tick(None);
        bool reproducible = c.StateChecksum() == a.StateChecksum();

        // Liveness — did the bots actually play? Judge against PEAKS over the match,
        // not the final frame: peasant breeding inflates the unit count and then a
        // battle cuts it back, so a snapshot at the end understates both.
        int builtP2 = a.Buildings.FindAll(x => x.Owner == 2 && x.Alive).Count;
        int builtP1 = a.Buildings.FindAll(x => x.Owner == 1 && x.Alive).Count;
        bool built = builtP1 > 1 && builtP2 > 1;              // more than just the starting keep
        bool trained = peakArmy1 > 3 && peakArmy2 > 3;        // both armed beyond the 3 they start with
        // A clash: the living-unit count fell from its peak (soldiers died) or a
        // keep took a hit.
        int keepHpP1 = KeepHp(a, 1), keepHpP2 = KeepHp(a, 2);
        int units = a.Units.Count;
        bool fought = units < peakUnits || keepHpP1 < FullKeep || keepHpP2 < FullKeep;

        Console.WriteLine("Stronghold — AI skirmish check");
        Console.WriteLine($"  ran {Ticks} ticks, bot vs bot, seed 0x{Simulation.DefaultSeed:X8}\n");
        Line(inSync,       "two sims stay in sync",       inSync ? "identical every tick" : $"DIVERGED at tick {firstDivergeTick}");
        Line(reproducible, "re-run reproduces state",     $"final 0x{a.StateChecksum():X8}");
        Line(built,        "bots raise buildings",        $"P1 {builtP1}, P2 {builtP2} standing");
        Line(trained,      "bots train an army",          $"peak P1 {peakArmy1}, P2 {peakArmy2} soldiers");
        Line(fought,       "the armies actually clash",   $"{peakUnits} units at peak, {units} left; keeps {keepHpP1}/{keepHpP2}");

        bool pass = inSync && reproducible && built && trained && fought;
        Console.WriteLine("\nRESULT: " + (pass ? "PASS — the bot plays, and plays deterministically." : "FAIL"));
        return pass ? 0 : 1;
    }

    const int FullKeep = 600;   // BuildHp[Keep]
    static int KeepHp(Simulation s, int owner)
    {
        foreach (var b in s.Buildings)
            if (b.Owner == owner && b.Type == BuildingType.Keep && b.Alive) return b.Hp;
        return 0;   // keep razed
    }

    static void Line(bool ok, string name, string detail) =>
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name,-32}  {detail}");
}
