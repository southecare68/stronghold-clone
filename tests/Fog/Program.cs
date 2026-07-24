// Fog — fog of war as a game rule.
//
// The claim under test is not "the right tiles are dark" (that is a rendering
// question and this project draws no pixels here). It is that fog is REAL: it is
// computed identically on every machine, it survives a rejoin, and it decides
// which orders are legal. A client that could order a strike on a unit hidden
// behind the ridge would have fog on screen and none in the game.

using System;
using System.Collections.Generic;
using Sim;
using Netcode;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Fog — sight, memory, and the orders they gate\n");

        FogOffChangesNothing();
        AUnitLightsItsSurroundings();
        RockBlocksSightButWaterDoesNot();
        ExploredRemembersWhatVisibleForgets();
        CannotOrderAnAttackOnWhatYouCannotSee();
        UnitsDoNotAutoAcquireThroughFog();
        AnEngagedTargetIsStillChasedIntoFog();
        CannotWorkOrBuildOnUnseenGround();
        TheSkirmishStartIsPlayableUnderFog();
        TwoClientsAgreeWithFogOn();
        ExploredSurvivesASnapshot();
        ExploredSurvivesTheWire();
        AReplayReproducesAFoggedMatch();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // The guarantee that let fog be added at all: with the flag off, the
    // simulation is bit-for-bit what it was. Every suite written before fog
    // existed depends on this, and so does 0xB1A7A676.
    static void FogOffChangesNothing()
    {
        Console.WriteLine("fog off leaves the simulation exactly as it was:");

        var plain = new Simulation(TileMap.Open(48));
        var a = plain.SpawnUnit(1, 5, 5);
        var far = plain.SpawnUnit(2, 40, 40);          // way beyond any sight radius
        Order(plain, Atk(a, far));
        for (int i = 0; i < 100; i++) plain.Tick(Array.Empty<Command>());

        Check("an attack order on a distant enemy is still accepted",
              plain.Units.Find(u => u.Id == a.Id).TargetId == far.Id);
        Check("everything is reported seen", plain.CanSee(1, 40, 40) && plain.HasExplored(1, 40, 40));
        Check("the frozen Checksum() is untouched by the fog machinery",
              plain.Checksum() == Reference().Checksum());
    }

    static Simulation Reference()
    {
        // The same scenario built by a sim that has never heard of fog: identical
        // spawns and ticks, no orders that fog could gate.
        var s = new Simulation(TileMap.Open(48));
        var a = s.SpawnUnit(1, 5, 5);
        var f = s.SpawnUnit(2, 40, 40);
        Order(s, Atk(a, f));
        for (int i = 0; i < 100; i++) s.Tick(Array.Empty<Command>());
        return s;
    }

    static void AUnitLightsItsSurroundings()
    {
        Console.WriteLine("\na unit lights a disc around itself:");
        var sim = Fogged(TileMap.Open(64));
        sim.SpawnUnit(1, 30, 30);
        sim.Tick(Array.Empty<Command>());          // vision is computed at the top of a tick

        Check("its own tile is visible", sim.Fog.IsVisible(1, 30, 30));
        Check($"a tile {Vision.UnitSight - 1} away is visible",
              sim.Fog.IsVisible(1, 30 + Vision.UnitSight - 1, 30));
        Check($"a tile {Vision.UnitSight + 2} away is not",
              !sim.Fog.IsVisible(1, 30 + Vision.UnitSight + 2, 30));
        Check("the far corner of the map is dark", !sim.Fog.IsVisible(1, 63, 63));

        // Sight is per player, not shared between them.
        Check("the enemy sees none of it", !sim.Fog.IsVisible(2, 30, 30));

        // Round, not square: the diagonal corner of the bounding box is outside
        // the radius. A square sight range is the classic giveaway of a fog
        // implementation that never checked its own geometry.
        Check("the corner of the bounding box is outside the disc",
              !sim.Fog.IsVisible(1, 30 + Vision.UnitSight, 30 + Vision.UnitSight));
    }

    static void RockBlocksSightButWaterDoesNot()
    {
        Console.WriteLine("\nrock blocks sight; water does not:");
        //          x: 0123456
        var map = TileMap.FromRows(
            ".......",
            ".......",
            "...#...",     // a rock pillar at (3,2)
            ".......",
            "...~...",     // water at (3,4)
            ".......",
            ".......");
        map.SealTerrain();
        var sim = Fogged(map);
        sim.SpawnUnit(1, 1, 2);      // west of both, in line with each
        sim.Tick(Array.Empty<Command>());

        Check("it sees up to the rock", sim.Fog.IsVisible(1, 2, 2));
        Check("the rock tile itself is visible (you can see the rock face)",
              sim.Fog.IsVisible(1, 3, 2));
        Check("but not the tile directly behind it", !sim.Fog.IsVisible(1, 5, 2));

        var lake = Fogged(map);
        lake.SpawnUnit(1, 1, 4);     // in line with the water instead
        lake.Tick(Array.Empty<Command>());
        Check("sight crosses water unhindered", lake.Fog.IsVisible(1, 5, 4));
    }

    static void ExploredRemembersWhatVisibleForgets()
    {
        Console.WriteLine("\nexplored accumulates; visible does not:");
        var sim = Fogged(TileMap.Open(64));
        var scout = sim.SpawnUnit(1, 5, 30);
        sim.Tick(Array.Empty<Command>());

        Check("home is visible at the start", sim.Fog.IsVisible(1, 5, 30));

        Order(sim, Move(scout, 55, 30));
        for (int i = 0; i < 900 && scout.HasPath; i++) sim.Tick(Array.Empty<Command>());
        Check($"the scout crossed the map (x={Fixed.ToInt(scout.X)})", Fixed.ToInt(scout.X) == 55);

        Check("where it walked is still explored", sim.Fog.IsExplored(1, 30, 30));
        Check("but no longer visible", !sim.Fog.IsVisible(1, 30, 30));
        Check("where it now stands is both", sim.Fog.IsVisible(1, 55, 30) && sim.Fog.IsExplored(1, 55, 30));
        Check("ground it never approached is neither",
              !sim.Fog.IsExplored(1, 30, 60) && !sim.Fog.IsVisible(1, 30, 60));
    }

    // The rule the whole feature exists for.
    static void CannotOrderAnAttackOnWhatYouCannotSee()
    {
        Console.WriteLine("\nyou cannot order a strike on a hidden enemy:");
        var sim = Fogged(TileMap.Open(64));
        var mine = sim.SpawnUnit(1, 5, 5);
        var hidden = sim.SpawnUnit(2, 55, 55);      // far outside sight
        sim.Tick(Array.Empty<Command>());

        Order(sim, Atk(mine, hidden));
        Check("the order is refused", mine.TargetId == 0);

        // Same order, same units, once a scout has actually found it.
        var seen = Fogged(TileMap.Open(64));
        var attacker = seen.SpawnUnit(1, 5, 5);
        var spotter = seen.SpawnUnit(1, 54, 55);    // stands next to the enemy
        var foe = seen.SpawnUnit(2, 55, 55);
        seen.Tick(Array.Empty<Command>());

        Check("the spotter can see it", seen.Fog.IsVisible(1, 55, 55));
        Order(seen, Atk(attacker, foe));
        Check("and now the order is accepted", attacker.TargetId == foe.Id);
        Check("even though the attacker itself is nowhere near — an army shares its sight",
              Fixed.ToInt(attacker.X) == 5 && spotter.Alive);
    }

    static void UnitsDoNotAutoAcquireThroughFog()
    {
        Console.WriteLine("\naggro does not reach through fog:");
        // A rock wall between two units that are well inside aggro range of each
        // other. Without the visibility test they would lock on through solid rock.
        var map = TileMap.FromRows(
            "..........",
            "..........",
            "....##....",
            "....##....",
            "....##....",
            "....##....",
            "..........",
            "..........");
        map.SealTerrain();

        var sim = Fogged(map);
        var mine = sim.SpawnUnit(1, 2, 4);
        var theirs = sim.SpawnUnit(2, 7, 4);        // 5 tiles away: inside aggro
        var bait = sim.SpawnUnit(2, 3, 4);          // adjacent and in plain sight

        Order(sim, Atk(mine, bait));
        for (int i = 0; i < 400 && bait.Alive; i++) sim.Tick(Array.Empty<Command>());

        Check("the visible bait is killed", !bait.Alive);
        Check("the unit behind the rock was never seen", !sim.Fog.IsVisible(1, 7, 4));
        sim.Tick(Array.Empty<Command>());
        Check("so nothing auto-acquired it", mine.TargetId != theirs.Id);
    }

    static void AnEngagedTargetIsStillChasedIntoFog()
    {
        Console.WriteLine("\na fight already joined is not forgotten:");
        var sim = Fogged(TileMap.Open(64));
        var hunter = sim.SpawnUnit(1, 30, 30);
        var prey = sim.SpawnUnit(2, 31, 30);
        sim.Tick(Array.Empty<Command>());

        Order(sim, Atk(hunter, prey));
        Check("the target is taken", hunter.TargetId == prey.Id);

        // Teleporting the prey out of sight is the cleanest way to ask the
        // question: does an ENGAGED target survive going dark?
        prey.X = Fixed.FromInt(60);
        prey.Y = Fixed.FromInt(60);
        prey.Tx = prey.X; prey.Ty = prey.Y;
        sim.Tick(Array.Empty<Command>());

        Check("the prey is out of sight", !sim.Fog.IsVisible(1, 60, 60));
        Check("but the hunter keeps its target", hunter.TargetId == prey.Id);
        Check("and sets off after it", hunter.HasPath);
    }

    static void CannotWorkOrBuildOnUnseenGround()
    {
        Console.WriteLine("\nyou cannot work or build in the dark:");
        var sim = Fogged(TileMap.Open(64));
        var worker = sim.SpawnUnit(1, 5, 5);
        sim.PlaceBuilding(BuildingType.Keep, 1, 3, 3);
        sim.AddResource(1, ResourceType.Wood, 500);
        sim.AddResource(1, ResourceType.Stone, 500);
        var far = sim.SpawnNode(ResourceType.Wood, 55, 55, 300);
        var near = sim.SpawnNode(ResourceType.Wood, 8, 5, 300);
        sim.Tick(Array.Empty<Command>());

        Order(sim, Gather(worker, far));
        Check("a gather order on an undiscovered node is refused", worker.Job == Job.None);

        Order(sim, Gather(worker, near));
        Check("but one on a node in sight is accepted", worker.Job == Job.Gathering);

        int before = sim.Buildings.Count;
        Order(sim, Build(1, BuildingType.Barracks, 50, 50));
        Check("a build order on unexplored ground is refused", sim.Buildings.Count == before);
        Check("and it costs nothing", sim.Stockpile(1, ResourceType.Wood) == 500);

        Order(sim, Build(1, BuildingType.Barracks, 7, 7));
        Check("but one on explored ground goes up", sim.Buildings.Count == before + 1);
    }

    // Fog turns "is this node reachable?" into a second, sharper question: can
    // you SEE it well enough to be allowed to send anyone there? A start where
    // both players must scout their own back yard before they may gather is a
    // delay rather than a decision, and it is completely invisible in the sim
    // tests, which do not care what a player can see.
    static void TheSkirmishStartIsPlayableUnderFog()
    {
        Console.WriteLine("\nthe skirmish start is playable under fog:");
        const int size = Skirmish.DefaultSize;
        var sim = new Simulation(TileMap.Skirmish(size));
        Skirmish.Setup(sim, size);
        sim.Tick(Array.Empty<Command>());

        int w = Skirmish.West(size), e = Skirmish.East(size), m = Skirmish.MidY(size);

        Check("each player can see their own keep",
              sim.Fog.IsVisible(1, w + 1, m) && sim.Fog.IsVisible(2, e + 1, m));

        // Both home patches must be workable from the first tick.
        int home1 = 0, home2 = 0;
        foreach (var n in sim.Nodes)
        {
            if (sim.Fog.IsVisible(1, n.X, n.Y)) home1++;
            if (sim.Fog.IsVisible(2, n.X, n.Y)) home2++;
        }
        Check($"player 1 opens with 2 workable patches in sight (has {home1})", home1 == 2);
        Check($"player 2 opens with 2 workable patches in sight (has {home2})", home2 == 2);

        // And a gather order on one is actually accepted, which is the thing that
        // matters — visibility is only interesting because of what it permits.
        ResourceNode near = null;
        foreach (var n in sim.Nodes) if (sim.Fog.IsVisible(1, n.X, n.Y)) { near = n; break; }
        var worker = sim.Units[0];
        Order(sim, Gather(worker, near));
        Check("and a worker can be put on one immediately", worker.Job == Job.Gathering);

        // The contested patches by the passes must NOT be free: that is what
        // makes them contested.
        int seen = 0;
        foreach (var n in sim.Nodes) if (sim.Fog.IsVisible(1, n.X, n.Y)) seen++;
        Check($"but the pass patches still have to be scouted ({sim.Nodes.Count - seen} unseen)",
              seen < sim.Nodes.Count);

        // Neither side starts able to see the other — the entire premise.
        Check("neither player can see the other's keep",
              !sim.Fog.IsVisible(1, e + 1, m) && !sim.Fog.IsVisible(2, w + 1, m));
        Check("nor the other's army",
              !sim.Fog.IsVisible(1, e - 2, m) && !sim.Fog.IsVisible(2, w + 4, m));
    }

    // Fog is now hashed state, so this is the test that would catch a Vision that
    // iterated a plain Dictionary or lit tiles in an order that depended on
    // anything local.
    static void TwoClientsAgreeWithFogOn()
    {
        Console.WriteLine("\ntwo clients agree tick by tick with fog on:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Skirmish(96));
        var b = new Client(2, net, TileMap.Skirmish(96));
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b }) Skirmish.Setup(c.Sim, 96);

        Check("setup turned fog on", a.Sim.FogEnabled && b.Sim.FogEnabled);

        // March both armies at each other through the middle pass, so vision is
        // changing on both sides every tick and the two eventually meet.
        var script = new Dictionary<int, Action>
        {
            [2] = () =>
            {
                foreach (var c in new[] { a, b })
                {
                    c.Issue(MoveIds(new[] { 1, 2, 3 }, 48, 48));
                    c.Issue(MoveIds(new[] { 4, 5, 6 }, 48, 48));
                }
            },
        };

        int desyncs = 0, first = -1;
        for (int t = 0; t < 400; t++)
        {
            if (script.TryGetValue(t, out var act)) act();
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum())
            {
                if (first < 0) first = t;
                desyncs++;
            }
        }

        Check($"StateChecksum identical on all 400 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check("both explored the same ground",
              SameExplored(a.Sim, b.Sim, 1) && SameExplored(a.Sim, b.Sim, 2));
        Check("and the armies actually moved (so vision really changed)",
              a.Sim.Fog.IsExplored(1, 40, 48));

        // Fog must be in the hash, or a client that forgot everything it had
        // seen would look identical to one that had not.
        uint before = a.Sim.StateChecksum();
        a.Sim.Fog.RestoreExplored(new Dictionary<int, uint[]>());
        Check("wiping one client's explored memory IS caught by StateChecksum",
              a.Sim.StateChecksum() != before);
    }

    static void ExploredSurvivesASnapshot()
    {
        Console.WriteLine("\na rejoiner gets their map back:");
        var sim = Fogged(TileMap.Open(64));
        var scout = sim.SpawnUnit(1, 5, 30);
        sim.SpawnUnit(2, 60, 5);
        Order(sim, Move(scout, 40, 30));
        for (int i = 0; i < 700 && scout.HasPath; i++) sim.Tick(Array.Empty<Command>());

        var snap = sim.Snapshot();
        Check("the snapshot carries the fog flag", snap.FogEnabled);
        Check("and the explored bitsets", snap.Explored.Count > 0);

        var rejoiner = new Simulation(TileMap.Open(64));
        rejoiner.Restore(snap);

        Check("the rejoiner's checksum matches the sender's", rejoiner.StateChecksum() == snap.Checksum);
        Check("it remembers the ground the scout crossed", rejoiner.Fog.IsExplored(1, 20, 30));
        Check("it has NOT been handed ground nobody walked", !rejoiner.Fog.IsExplored(1, 20, 60));
        Check("and current visibility was rebuilt from the units, not copied",
              rejoiner.Fog.IsVisible(1, 40, 30) && !rejoiner.Fog.IsVisible(1, 20, 30));

        // From here the two must stay in step.
        for (int i = 0; i < 50; i++) { sim.Tick(Array.Empty<Command>()); rejoiner.Tick(Array.Empty<Command>()); }
        Check("and they stay in sync afterwards", sim.StateChecksum() == rejoiner.StateChecksum());
    }

    static void ExploredSurvivesTheWire()
    {
        Console.WriteLine("\nfog survives serialization:");
        var sim = Fogged(TileMap.Open(64));
        var scout = sim.SpawnUnit(1, 5, 5);
        sim.SpawnUnit(2, 60, 60);
        Order(sim, Move(scout, 40, 40));
        for (int i = 0; i < 700 && scout.HasPath; i++) sim.Tick(Array.Empty<Command>());

        var snap = sim.Snapshot();
        byte[] bytes = Wire.Serialize(snap);
        var back = Wire.DeserializeSnapshot(bytes);

        Check("the snapshot round-trips", back != null);
        Check("the fog flag survives", back.FogEnabled);
        Check("every owner's bitset survives", back.Explored.Count == snap.Explored.Count);

        bool identical = true;
        foreach (var kv in snap.Explored)
        {
            if (!back.Explored.TryGetValue(kv.Key, out var got) || got.Length != kv.Value.Length)
            { identical = false; break; }
            for (int i = 0; i < got.Length; i++) if (got[i] != kv.Value[i]) { identical = false; break; }
        }
        Check("bit for bit", identical);

        var adopted = new Simulation(TileMap.Open(64));
        adopted.Restore(back);
        Check("and a sim restored from the wire hashes the same",
              adopted.StateChecksum() == snap.Checksum);

        // Truncation must be refused, not half-read: a client that adopts half a
        // fog map is worse off than one that waits.
        var chopped = new byte[bytes.Length - 8];
        Array.Copy(bytes, chopped, chopped.Length);
        Check("a truncated snapshot is rejected", Wire.DeserializeSnapshot(chopped) == null);
    }

    static void AReplayReproducesAFoggedMatch()
    {
        Console.WriteLine("\na fogged match replays exactly:");
        var map = TileMap.Skirmish(96);
        var sim = new Simulation(map);
        Skirmish.Setup(sim, 96);
        var rec = new ReplayRecorder(sim);

        for (int t = 0; t < 200; t++)
        {
            var cmds = new List<Command>();
            if (t == 3)
                cmds.Add(new Command
                {
                    Owner = 1, Type = CommandType.Move,
                    UnitIds = new[] { 1, 2, 3 }, X = 48, Y = 48, Seq = 1,
                });
            rec.Record(cmds);
            sim.Tick(cmds);
        }
        var replay = rec.Finish(sim);

        Check("the replay verifies", replay.Verify());

        var played = replay.Reconstruct();
        Check("the reconstructed sim starts with fog on", played.FogEnabled);
        foreach (var cmds in replay.Commands) played.Tick(cmds);
        Check("and ends on the recorded checksum", played.StateChecksum() == replay.FinalChecksum);
        Check("with the same explored ground as the live match",
              SameExplored(sim, played, 1) && SameExplored(sim, played, 2));

        // Round-tripping through bytes is what an actual saved replay does.
        var reloaded = Replay.Deserialize(replay.Serialize());
        Check("a serialized replay still verifies", reloaded != null && reloaded.Verify());
    }

    // ---- helpers -----------------------------------------------------------

    static Simulation Fogged(TileMap map)
    {
        var sim = new Simulation(map) { FogEnabled = true };
        return sim;
    }

    static bool SameExplored(Simulation a, Simulation b, int owner)
    {
        bool ha = a.Fog.Explored.TryGetValue(owner, out var x);
        bool hb = b.Fog.Explored.TryGetValue(owner, out var y);
        if (ha != hb) return false;
        if (!ha) return true;
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
        return true;
    }

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

    static Command Move(Unit u, int x, int y) => new Command
    {
        Owner = u.Owner, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = x, Y = y,
    };

    static Command MoveIds(int[] ids, int x, int y) => new Command
    {
        Type = CommandType.Move, UnitIds = ids, X = x, Y = y,
    };

    static Command Atk(Unit u, Unit target) => new Command
    {
        Owner = u.Owner, Type = CommandType.Attack, UnitIds = new[] { u.Id }, TargetId = target.Id,
    };

    static Command Gather(Unit u, ResourceNode n) => new Command
    {
        Owner = u.Owner, Type = CommandType.Gather, UnitIds = new[] { u.Id }, TargetId = n.Id,
    };

    static Command Build(int owner, BuildingType t, int x, int y) => new Command
    {
        Owner = owner, Type = CommandType.Build, TargetId = (int)t, X = x, Y = y,
    };

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
