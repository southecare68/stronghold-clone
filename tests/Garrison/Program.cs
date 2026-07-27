// Garrison — archers on the walls.
//
// A soldier ordered onto a friendly rampart climbs onto it and holds there,
// firing at any enemy in reach with no order given: it shoots two tiles further
// than on the ground (height) and takes half the damage (cover). A move order
// pulls it back down, and a wall battered to rubble drops its garrison. As ever,
// the failures that bite are determinism ones, so the two-client check is the
// point — but the mechanic itself is pinned down step by step first.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    // A ranged design: RangeStat 8 == 4 tiles on the ground, 6 from a wall.
    static readonly UnitDesign Archer =
        new UnitDesign { Hp = 55, Damage = 9, SpeedStat = 6, RangeStat = 8, Cooldown = 13 };

    static void Main()
    {
        Console.WriteLine("Garrison — archers on the walls\n");

        ASoldierClimbsOntoTheWall();
        AGarrisonedArcherAutoFires();
        TheWallExtendsTheArchersReach();
        AGarrisonTakesCover();
        RazingTheWallDismissesTheGarrison();
        AMoveOrderPullsThemOffTheWall();
        PeasantsCannotGarrison();
        TwoClientsAgreeOnGarrison();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // Ordered to a wall it stands beside, a soldier climbs onto the rampart —
    // GarrisonId set and its position snapped onto the wall's tile.
    static void ASoldierClimbsOntoTheWall()
    {
        Console.WriteLine("a soldier climbs onto the wall:");
        var sim = new Simulation(TileMap.Open(48));
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10); sim.PlaceBuilding(BuildingType.Steps, 1, 12, 10);
        var u = sim.SpawnUnit(1, 10, 12);          // two tiles below the wall

        Order(sim, Garrison(u, wall.Id));
        for (int i = 0; i < 20; i++) sim.Tick(Array.Empty<Command>());

        Check("it is manning the wall", u.GarrisonId == wall.Id);
        Check($"and stands on the rampart tile ({u.X >> 16},{u.Y >> 16})",
              (u.X >> 16) == 10 && (u.Y >> 16) == 10);
    }

    // A garrisoned archer needs no order: an enemy that walks into reach is shot.
    static void AGarrisonedArcherAutoFires()
    {
        Console.WriteLine("\na garrisoned archer auto-fires with no order:");
        var sim = new Simulation(TileMap.Open(48));
        int archer = sim.RegisterDesign(Archer);
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10); sim.PlaceBuilding(BuildingType.Steps, 1, 12, 10);
        var a = sim.SpawnUnit(1, 10, 11, archer);
        Garrison(sim, a, wall);

        var enemy = sim.SpawnUnit(2, 13, 10);      // 3 tiles east, no order given to anyone
        int hp0 = enemy.Hp;
        for (int i = 0; i < 60; i++) sim.Tick(Array.Empty<Command>());
        Check($"the enemy was shot from the wall ({hp0} -> {enemy.Hp})", enemy.Hp < hp0);
        Check("the archer never left its post", (a.X >> 16) == 10 && (a.Y >> 16) == 10);
    }

    // From the wall the archer reaches two tiles further: it hits a foe at 5 tiles
    // that is outside its 4-tile ground range, while a foe at 7 tiles stays safe.
    static void TheWallExtendsTheArchersReach()
    {
        Console.WriteLine("\nthe wall extends the archer's reach:");
        var sim = new Simulation(TileMap.Open(48));
        int archer = sim.RegisterDesign(Archer);
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10); sim.PlaceBuilding(BuildingType.Steps, 1, 12, 10);
        var a = sim.SpawnUnit(1, 10, 11, archer);
        Garrison(sim, a, wall);

        var near = sim.SpawnUnit(2, 15, 10);       // 5 tiles: out of ground range, in wall range
        var far = sim.SpawnUnit(2, 17, 10);        // 7 tiles: beyond even the wall's reach
        int nearHp = near.Hp, farHp = far.Hp;
        for (int i = 0; i < 80; i++) sim.Tick(Array.Empty<Command>());

        Check($"the foe at 5 tiles is hit from the wall ({nearHp} -> {near.Hp})", near.Hp < nearHp);
        Check($"the foe at 7 tiles is out of reach ({far.Hp})", far.Hp == farHp);
        Check("and the archer held the wall to shoot", (a.X >> 16) == 10 && (a.Y >> 16) == 10);
    }

    // Under cover a garrisoned soldier takes half the damage. Run the same attack
    // from the same seed — the defender on the wall's tile either way, so an enemy
    // archer shoots the exact same rolls — and toggle only whether it is manning
    // the wall. The manned run loses about half as much.
    static void AGarrisonTakesCover()
    {
        Console.WriteLine("\na garrison takes cover from the wall:");
        int exposedLoss = AttackLoss(garrison: false);
        int coverLoss = AttackLoss(garrison: true);
        Check($"cover cut the damage taken ({coverLoss} on the wall vs {exposedLoss} exposed)",
              coverLoss < exposedLoss);
        Check($"and it was roughly halved ({coverLoss} vs ~{exposedLoss / 2})",
              coverLoss <= exposedLoss / 2 + 5 && coverLoss >= exposedLoss / 2 - 2);
    }

    // Same fight each way: the defender stands on the wall's tile, an enemy archer
    // four tiles off shoots it. Only difference between the runs is whether the
    // defender is GARRISONED (cover) — so the rolls match and the drop is halving.
    static int AttackLoss(bool garrison)
    {
        var sim = new Simulation(TileMap.Open(48));
        int archer = sim.RegisterDesign(Archer);
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10); sim.PlaceBuilding(BuildingType.Steps, 1, 12, 10);
        var target = sim.SpawnUnit(1, 10, 10);     // on the wall tile in both runs
        if (garrison) Garrison(sim, target, wall);

        var attacker = sim.SpawnUnit(2, 10, 14, archer);   // enemy archer, exactly 4 tiles away
        int hp0 = target.Hp;
        Order(sim, Attack(attacker, target));
        for (int i = 0; i < 70; i++) sim.Tick(Array.Empty<Command>());   // a handful of volleys, target survives
        return hp0 - target.Hp;
    }

    // A wall beaten to rubble drops its garrison back to the ground, still alive.
    static void RazingTheWallDismissesTheGarrison()
    {
        Console.WriteLine("\nrazing the wall dismisses the garrison:");
        var sim = new Simulation(TileMap.Open(48));
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10); sim.PlaceBuilding(BuildingType.Steps, 1, 12, 10);
        var u = sim.SpawnUnit(1, 10, 11);
        Garrison(sim, u, wall);
        Check("it is on the wall", u.GarrisonId == wall.Id && (u.X >> 16) == 10 && (u.Y >> 16) == 10);

        wall.Hp = 0;
        sim.Tick(Array.Empty<Command>());          // RemoveDestroyedBuildings runs
        Check("the wall is gone", FindBuilding(sim, wall.Id) == null);
        Check("its garrison was dismissed", u.GarrisonId == 0 && u.Alive);
    }

    // A move order climbs a soldier down off the wall and sends it on its way.
    static void AMoveOrderPullsThemOffTheWall()
    {
        Console.WriteLine("\na move order pulls them off the wall:");
        var sim = new Simulation(TileMap.Open(48));
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10); sim.PlaceBuilding(BuildingType.Steps, 1, 12, 10);
        var u = sim.SpawnUnit(1, 10, 11);
        Garrison(sim, u, wall);
        Check("it starts on the wall", u.GarrisonId == wall.Id);

        Order(sim, Move(u, 20, 20));
        Check("the move dismissed the garrison", u.GarrisonId == 0);
        for (int i = 0; i < 400 && u.HasPath; i++) sim.Tick(Array.Empty<Command>());
        Check($"and it walked away ({u.X >> 16},{u.Y >> 16})", (u.X >> 16) == 20 && (u.Y >> 16) == 20);
    }

    // Peasants are workers, not a garrison — a garrison order passes them by.
    static void PeasantsCannotGarrison()
    {
        Console.WriteLine("\npeasants cannot garrison:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 5, 5);
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10); sim.PlaceBuilding(BuildingType.Steps, 1, 12, 10);
        var p = sim.SpawnPeasant(1);
        Order(sim, Garrison(p, wall.Id));
        for (int i = 0; i < 10; i++) sim.Tick(Array.Empty<Command>());
        Check("the peasant did not man the wall", p.GarrisonId == 0);
    }

    // The whole thing, computed twice, must agree every tick — the climb, the
    // auto-fire, and the damage rolls.
    static void TwoClientsAgreeOnGarrison()
    {
        Console.WriteLine("\ntwo clients agree on the garrison:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Open(48));
        var b = new Client(2, net, TileMap.Open(48));
        net.Connect(a);
        net.Connect(b);

        foreach (var c in new[] { a, b })
        {
            int archer = c.Sim.RegisterDesign(Archer);
            var wall = c.Sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10); c.Sim.PlaceBuilding(BuildingType.Steps, 1, 12, 10);
            var ar = c.Sim.SpawnUnit(1, 10, 11, archer);
            c.Sim.SpawnUnit(2, 14, 10);            // an enemy in wall range
            // Garrison via a queued command so both clients apply it in lockstep.
            c.Issue(new Command { Type = CommandType.Garrison, UnitIds = new[] { ar.Id }, TargetId = wall.Id });
        }

        int desyncs = 0, first = -1;
        for (int t = 0; t < 300; t++)
        {
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical on all 300 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
    }

    // ---- helpers -----------------------------------------------------------

    // Garrison a unit and run enough ticks for it to climb on.
    static void Garrison(Simulation sim, Unit u, Building wall)
    {
        Order(sim, Garrison(u, wall.Id));
        for (int i = 0; i < 20 && u.GarrisonId != 0 && ((u.X >> 16) != wall.X || (u.Y >> 16) != wall.Y); i++)
            sim.Tick(Array.Empty<Command>());
    }

    static Command Garrison(Unit u, int wallId) => new Command
    { Owner = u.Owner, Type = CommandType.Garrison, UnitIds = new[] { u.Id }, TargetId = wallId };

    static Command Attack(Unit u, Unit target) => new Command
    { Owner = u.Owner, Type = CommandType.Attack, UnitIds = new[] { u.Id }, TargetId = target.Id };

    static Command Move(Unit u, int x, int y) => new Command
    { Owner = u.Owner, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = x, Y = y };

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

    static Building FindBuilding(Simulation sim, int id)
    {
        foreach (var b in sim.Buildings) if (b.Id == id) return b;
        return null;
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
