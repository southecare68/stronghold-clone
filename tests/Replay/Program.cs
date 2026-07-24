// Replay — record a match, play it back bit-for-bit.

using System;
using System.Collections.Generic;
using Sim;
using Netcode;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Replay — record and reproduce a match exactly\n");

        AReplayReproducesTheMatch();
        ReplayIsStableAcrossPlaybacks();
        AReplaySurvivesSaveAndLoad();
        AReplayTracksTheRunTickForTick();
        EmptyAndMalformedBytesAreRefused();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // Record a busy match — economy, combat, buildings, mixed designs, on a map
    // with obstacles — then play it back and demand the identical final world.
    static Replay RecordABusyMatch(out uint liveChecksum, out int liveUnits)
    {
        var sim = new Simulation(TileMap.Demo(48));
        int runner = sim.RegisterDesign(new UnitDesign { Hp = 60, Damage = 9, SpeedStat = 10, RangeStat = 3, Cooldown = 10 });
        int brute = sim.RegisterDesign(new UnitDesign { Hp = 150, Damage = 11, SpeedStat = 3, RangeStat = 3, Cooldown = 15 });

        sim.SetDropOff(1, 6, 6);
        sim.SetDropOff(2, 42, 42);
        sim.SpawnUnit(1, 6, 6);              // 1
        sim.SpawnUnit(1, 7, 6, runner);      // 2
        sim.SpawnUnit(2, 42, 42);            // 3
        sim.SpawnUnit(2, 43, 42, brute);     // 4
        sim.SpawnNode(ResourceType.Wood, 12, 6, 200);   // node 1
        sim.AddResource(1, ResourceType.Wood, 100);
        sim.AddResource(1, ResourceType.Stone, 100);

        // The script of orders, keyed by the tick they're issued on.
        var script = new Dictionary<int, List<Command>>
        {
            [1]  = Cmds(Move(1, 12, 6), Gather(1, 1)),
            [3]  = Cmds(Build(1, BuildingType.Wall, 10, 10)),
            [20] = Cmds(Attack(2, 4)),
            [25] = Cmds(Move(3, 20, 20)),
            [60] = Cmds(Build(1, BuildingType.Barracks, 4, 12)),
            [90] = Cmds(Train(1, 2, brute)),      // barracks is building id 2
        };

        var rec = new ReplayRecorder(sim);
        for (int t = 0; t < 400; t++)
        {
            var cmds = script.TryGetValue(t, out var list) ? list : Empty;
            rec.Record(cmds);
            sim.Tick(cmds);
        }

        liveChecksum = sim.StateChecksum();
        liveUnits = sim.Units.Count;
        return rec.Finish(sim);
    }

    static void AReplayReproducesTheMatch()
    {
        Console.WriteLine("a replay reproduces the match:");
        var replay = RecordABusyMatch(out uint live, out int liveUnits);

        Check($"recorded {replay.Commands.Count} ticks", replay.Commands.Count == 400);
        Check("the recorded final checksum matches the live run", replay.FinalChecksum == live);

        uint played = replay.Play();
        Check($"playback lands on the same checksum (0x{played:X8} vs 0x{live:X8})", played == live);
        Check("the recording verifies itself", replay.Verify());

        // Sanity: the match actually did something (units built/killed).
        var rebuilt = replay.Reconstruct();
        Check("reconstruct starts at the recorded start tick", rebuilt.TickNumber == replay.StartTick);
    }

    static void ReplayIsStableAcrossPlaybacks()
    {
        Console.WriteLine("\nreplaying twice gives the same result:");
        var replay = RecordABusyMatch(out _, out _);
        uint a = replay.Play();
        uint b = replay.Play();
        Check($"two playbacks agree (0x{a:X8})", a == b);
        Check("and both match the recording", a == replay.FinalChecksum);
    }

    static void AReplaySurvivesSaveAndLoad()
    {
        Console.WriteLine("\na replay survives save and load:");
        var replay = RecordABusyMatch(out uint live, out _);

        byte[] bytes = replay.Serialize();
        Check($"serialized to {bytes.Length} bytes", bytes.Length > 0);

        var loaded = Replay.Deserialize(bytes);
        Check("deserialized cleanly", loaded != null);
        if (loaded == null) return;

        Check("the loaded replay has the same tick count", loaded.Commands.Count == replay.Commands.Count);
        Check("the loaded final checksum matches", loaded.FinalChecksum == live);
        Check("and a loaded replay plays back to the same world", loaded.Play() == live);

        // A saved-then-loaded replay must be byte-identical when re-saved.
        byte[] again = loaded.Serialize();
        bool identical = again.Length == bytes.Length;
        for (int i = 0; identical && i < bytes.Length; i++) identical = again[i] == bytes[i];
        Check("re-serializing is byte-identical", identical);
    }

    // Reproducing the final NUMBER could in principle happen by luck; this walks
    // both the live run and the playback in lockstep and checks every tick.
    static void AReplayTracksTheRunTickForTick()
    {
        Console.WriteLine("\nthe replay matches the run every tick, not just at the end:");
        // Re-run the recording live while stepping a reconstruction alongside it.
        var replay = RecordABusyMatch(out _, out _);
        var playback = replay.Reconstruct();

        int desyncs = 0, firstAt = -1;
        for (int i = 0; i < replay.Commands.Count; i++)
        {
            playback.Tick(replay.Commands[i]);
            // The recording holds the live final checksum; here we just confirm
            // the playback never NaNs out — full tick-parity vs a second live run:
        }

        // Now do a genuine side-by-side: a fresh live run and a fresh playback.
        var live = FreshLiveRun(out var liveChecks);
        var play2 = replay.Reconstruct();
        for (int i = 0; i < replay.Commands.Count; i++)
        {
            play2.Tick(replay.Commands[i]);
            if (play2.StateChecksum() != liveChecks[i])
            {
                if (firstAt < 0) firstAt = i;
                desyncs++;
            }
        }
        Check($"playback checksum equals the live checksum on all {replay.Commands.Count} ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {firstAt})" : ""), desyncs == 0);
        _ = live; _ = playback;
    }

    static void EmptyAndMalformedBytesAreRefused()
    {
        Console.WriteLine("\nbad replay bytes are refused:");
        Check("null", Replay.Deserialize(null) == null);
        Check("empty", Replay.Deserialize(Array.Empty<byte>()) == null);
        Check("garbage header", Replay.Deserialize(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) == null);

        var good = RecordABusyMatch(out _, out _).Serialize();
        var truncated = new byte[good.Length - 5];
        Array.Copy(good, truncated, truncated.Length);
        Check("truncated replay", Replay.Deserialize(truncated) == null);
        Check("the good replay still loads", Replay.Deserialize(good) != null);
    }

    // A second identical live run, recording each tick's checksum, for the
    // tick-for-tick comparison above.
    static Simulation FreshLiveRun(out uint[] checks)
    {
        var sim = new Simulation(TileMap.Demo(48));
        int runner = sim.RegisterDesign(new UnitDesign { Hp = 60, Damage = 9, SpeedStat = 10, RangeStat = 3, Cooldown = 10 });
        int brute = sim.RegisterDesign(new UnitDesign { Hp = 150, Damage = 11, SpeedStat = 3, RangeStat = 3, Cooldown = 15 });
        sim.SetDropOff(1, 6, 6);
        sim.SetDropOff(2, 42, 42);
        sim.SpawnUnit(1, 6, 6);
        sim.SpawnUnit(1, 7, 6, runner);
        sim.SpawnUnit(2, 42, 42);
        sim.SpawnUnit(2, 43, 42, brute);
        sim.SpawnNode(ResourceType.Wood, 12, 6, 200);
        sim.AddResource(1, ResourceType.Wood, 100);
        sim.AddResource(1, ResourceType.Stone, 100);

        var script = new Dictionary<int, List<Command>>
        {
            [1]  = Cmds(Move(1, 12, 6), Gather(1, 1)),
            [3]  = Cmds(Build(1, BuildingType.Wall, 10, 10)),
            [20] = Cmds(Attack(2, 4)),
            [25] = Cmds(Move(3, 20, 20)),
            [60] = Cmds(Build(1, BuildingType.Barracks, 4, 12)),
            [90] = Cmds(Train(1, 2, brute)),
        };

        checks = new uint[400];
        for (int t = 0; t < 400; t++)
        {
            var cmds = script.TryGetValue(t, out var list) ? list : Empty;
            sim.Tick(cmds);
            checks[t] = sim.StateChecksum();
        }
        return sim;
    }

    // ---- helpers -----------------------------------------------------------

    static readonly List<Command> Empty = new();

    static List<Command> Cmds(params Command[] cs) => new List<Command>(cs);

    static Command Move(int unit, int x, int y) => new Command
    { Owner = OwnerOf(unit), Type = CommandType.Move, UnitIds = new[] { unit }, X = x, Y = y };
    static Command Gather(int unit, int nodeId) => new Command
    { Owner = OwnerOf(unit), Type = CommandType.Gather, UnitIds = new[] { unit }, TargetId = nodeId };
    static Command Attack(int unit, int target) => new Command
    { Owner = OwnerOf(unit), Type = CommandType.Attack, UnitIds = new[] { unit }, TargetId = target };
    static Command Build(int owner, BuildingType t, int x, int y) => new Command
    { Owner = owner, Type = CommandType.Build, TargetId = (int)t, X = x, Y = y };
    static Command Train(int owner, int barracks, int design) => new Command
    { Owner = owner, Type = CommandType.Train, TargetId = barracks, X = design };

    // In this fixed scenario, units 1,2 are player 1 and 3,4 are player 2.
    static int OwnerOf(int unit) => unit <= 2 ? 1 : 2;

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
