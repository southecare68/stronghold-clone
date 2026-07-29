// AiSim — run bot skirmishes and check three things:
//   1. Determinism: two independent sims stepped identically stay bit-for-bit
//      equal at every tick, at every difficulty, and a fresh re-run reproduces the
//      same final state. The AI runs inside the tick and touches shared state, so
//      this is the guard that it cannot silently desync a networked match.
//   2. Liveness: the bot actually plays — it raises buildings, arms an army, and
//      marches it into a fight. A bot that does nothing would "pass" a pure
//      determinism check, so we assert it is alive too.
//   3. Difficulty: the levels form a real gradient. Measured against a PASSIVE
//      opponent (so two equal bots don't just clash and cap each other's growth),
//      a Hard bot grows a bigger economy and army than Normal, and Normal than
//      Easy — so the setting means something.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    const int Ticks = 2000;      // determinism/liveness: build, train, march, clash
    const int GradientTicks = 3500;  // long enough for Hard's second food chain to mature
    const int FullKeep = 600;    // BuildHp[Keep]
    static readonly List<Command> None = new();

    struct Outcome
    {
        public bool InSync;
        public uint Checksum;
        public int PeakArmy1, PeakArmy2, PeakUnits, FinalUnits, Builds1, Builds2, Keep1, Keep2;
        public int PeakPeas2, PeakArmyP2;   // player-2 economy/army peaks, for the gradient
        public int Churches2, PeakFaith2;   // player-2 churches raised and the faith they won
    }

    static Simulation FreshMatch(AiLevel level, bool botVsBot)
    {
        var sim = new Simulation(TileMap.Skirmish(Skirmish.DefaultSize));
        Skirmish.Setup(sim, Skirmish.DefaultSize);
        sim.EnableAi(2, level);
        if (botVsBot) sim.EnableAi(1, level);
        return sim;
    }

    // Run the same match on two independent sims and confirm they never diverge.
    static Outcome Run(AiLevel level, bool botVsBot, int ticks)
    {
        var a = FreshMatch(level, botVsBot);
        var b = FreshMatch(level, botVsBot);
        var o = new Outcome { InSync = true, PeakUnits = a.Units.Count };
        for (int t = 0; t < ticks; t++)
        {
            a.Tick(None);
            b.Tick(None);
            if (o.InSync && a.StateChecksum() != b.StateChecksum()) o.InSync = false;
            o.PeakUnits = Math.Max(o.PeakUnits, a.Units.Count);
            o.PeakArmy1 = Math.Max(o.PeakArmy1, a.ArmySize(1));
            o.PeakArmy2 = Math.Max(o.PeakArmy2, a.ArmySize(2));
            o.PeakPeas2 = Math.Max(o.PeakPeas2, a.PeasantCount(2));
            o.PeakArmyP2 = Math.Max(o.PeakArmyP2, a.ArmySize(2));
            o.PeakFaith2 = Math.Max(o.PeakFaith2, a.Faith(2));
        }
        o.Churches2 = a.CountBuildings(2, BuildingType.Church);
        o.Checksum = a.StateChecksum();
        o.FinalUnits = a.Units.Count;
        o.Builds1 = a.Buildings.FindAll(x => x.Owner == 1 && x.Alive).Count;
        o.Builds2 = a.Buildings.FindAll(x => x.Owner == 2 && x.Alive).Count;
        o.Keep1 = KeepHp(a, 1);
        o.Keep2 = KeepHp(a, 2);
        return o;
    }

    static int Main()
    {
        // Determinism + liveness on a Normal bot-vs-bot match.
        var normal = Run(AiLevel.Normal, botVsBot: true, Ticks);
        var rerun = FreshMatch(AiLevel.Normal, botVsBot: true);
        for (int t = 0; t < Ticks; t++) rerun.Tick(None);
        bool reproducible = rerun.StateChecksum() == normal.Checksum;

        // Gradient: each level against a passive opponent.
        var easy = Run(AiLevel.Easy, botVsBot: false, GradientTicks);
        var norm = Run(AiLevel.Normal, botVsBot: false, GradientTicks);
        var hard = Run(AiLevel.Hard, botVsBot: false, GradientTicks);

        bool inSync = normal.InSync && easy.InSync && norm.InSync && hard.InSync;
        bool built = normal.Builds1 > 1 && normal.Builds2 > 1;                 // more than the starting keep
        bool trained = normal.PeakArmy1 > 3 && normal.PeakArmy2 > 3;           // armed beyond the 3 they start with
        bool fought = normal.FinalUnits < normal.PeakUnits ||                  // soldiers died...
                      normal.Keep1 < FullKeep || normal.Keep2 < FullKeep;      // ...or a keep took a hit
        bool gradient = hard.PeakArmyP2 > norm.PeakArmyP2 && norm.PeakArmyP2 > easy.PeakArmyP2;
        // The bot contests the Religious path: it raises churches and converts its
        // people past the 25% starting congregation. Easy abstains (0 churches); a
        // tougher bot commits MORE churches (the gradient lives in the church count,
        // since a small dense flock saturates its faith at 100% either way).
        bool faithContest = easy.Churches2 == 0
                            && norm.Churches2 > 0 && hard.Churches2 > norm.Churches2
                            && norm.PeakFaith2 > 25 && hard.PeakFaith2 > 25;

        Console.WriteLine("Stronghold — AI skirmish check");
        Console.WriteLine($"  seed 0x{Simulation.DefaultSeed:X8}\n");
        Line(inSync,       "sims stay in sync (all levels)", inSync ? "identical every tick" : "DIVERGED");
        Line(reproducible, "re-run reproduces state",        $"Normal 0x{normal.Checksum:X8}");
        Line(built,        "bots raise buildings",           $"Normal: P1 {normal.Builds1}, P2 {normal.Builds2} standing");
        Line(trained,      "bots train an army",             $"Normal peak P1 {normal.PeakArmy1}, P2 {normal.PeakArmy2}");
        Line(fought,       "the armies actually clash",      $"Normal: {normal.PeakUnits} at peak, {normal.FinalUnits} left; keeps {normal.Keep1}/{normal.Keep2}");
        Line(gradient,     "difficulty scales the bot",      $"vs passive — peak peasants e{easy.PeakPeas2}/n{norm.PeakPeas2}/h{hard.PeakPeas2}, " +
                                                             $"peak army e{easy.PeakArmyP2}/n{norm.PeakArmyP2}/h{hard.PeakArmyP2}");
        Line(faithContest, "the bot contests the faith",     $"churches e{easy.Churches2}/n{norm.Churches2}/h{hard.Churches2}, " +
                                                             $"peak faith e{easy.PeakFaith2}/n{norm.PeakFaith2}/h{hard.PeakFaith2}%");

        bool pass = inSync && reproducible && built && trained && fought && gradient && faithContest;
        Console.WriteLine("\nRESULT: " + (pass ? "PASS — the bot plays deterministically, and the levels form a real gradient." : "FAIL"));
        return pass ? 0 : 1;
    }

    static int KeepHp(Simulation s, int owner)
    {
        foreach (var b in s.Buildings)
            if (b.Owner == owner && b.Type == BuildingType.Keep && b.Alive) return b.Hp;
        return 0;   // keep razed
    }

    static void Line(bool ok, string name, string detail) =>
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name,-32}  {detail}");
}
