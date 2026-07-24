// PointBuy — unit designs drive stats within a point budget.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("PointBuy — designs drive stats within a budget\n");

        TheBudgetIsEnforced();
        TheDefaultSoldierIsUnchanged();
        AFastDesignOutrunsASlowOne();
        ATankyDesignOutlastsAFragileOne();
        HighDamageKillsFaster();
        ARangedDesignHitsFromAfar();
        TwoClientsAgreeWithMixedDesigns();
        DesignsSurviveARejoin();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    static void TheBudgetIsEnforced()
    {
        Console.WriteLine("the point budget is enforced:");
        var sim = new Simulation(TileMap.Open(32));

        Check($"the budget is the default soldier's cost ({Simulation.MaxDesignPoints})",
              UnitDesign.DefaultSoldier().PointCost == Simulation.MaxDesignPoints);

        // A design at exactly the budget is accepted.
        int ok = sim.RegisterDesign(new UnitDesign { Hp = 60, Damage = 9, SpeedStat = 10, RangeStat = 3, Cooldown = 10 });
        Check("a design within budget is registered (id > 0)", ok == 1);

        // One over budget is rejected.
        int bad = sim.RegisterDesign(new UnitDesign { Hp = 300, Damage = 20, SpeedStat = 10, RangeStat = 6, Cooldown = 5 });
        Check("a design over budget is refused (-1)", bad == -1);
        Check("and the roster still only has the default + the good one", sim.Designs.Count == 2);
    }

    // Spawning through the default design must land on the pre-point-buy stats,
    // which is what keeps every older test — and 0xB1A7A676 — valid.
    static void TheDefaultSoldierIsUnchanged()
    {
        Console.WriteLine("\nthe default soldier is unchanged:");
        var sim = new Simulation(TileMap.Open(32));
        var u = sim.SpawnUnit(1, 5, 5);
        Check("default unit spawns with 100 HP", u.Hp == 100 && u.MaxHp == 100);
        Check("and design id 0", u.DesignId == 0);
        Check("and moves at 1/8 tile per tick",
              sim.DesignOf(0).SpeedFixed == Fixed.One / 8);
    }

    static void AFastDesignOutrunsASlowOne()
    {
        Console.WriteLine("\na fast design outruns a slow one:");
        var sim = new Simulation(TileMap.Open(64));
        int fast = sim.RegisterDesign(new UnitDesign { Hp = 60, Damage = 8, SpeedStat = 10, RangeStat = 3, Cooldown = 12 });
        int slow = sim.RegisterDesign(new UnitDesign { Hp = 150, Damage = 11, SpeedStat = 3, RangeStat = 3, Cooldown = 15 });
        Check("both designs registered", fast == 1 && slow == 2);

        var runner = sim.SpawnUnit(1, 2, 2, fast);
        var plodder = sim.SpawnUnit(1, 2, 2, slow);
        Order(sim, Move(runner, 50, 2));
        Order(sim, Move(plodder, 50, 2));
        for (int i = 0; i < 100; i++) sim.Tick(Array.Empty<Command>());

        Check($"the fast unit is ahead (runner x={Fixed.ToInt(runner.X)}, plodder x={Fixed.ToInt(plodder.X)})",
              runner.X > plodder.X);
    }

    static void ATankyDesignOutlastsAFragileOne()
    {
        Console.WriteLine("\na tanky design outlasts a fragile one:");
        // Two identical attackers hit two different-HP targets; the fragile one
        // dies first.
        var sim = new Simulation(TileMap.Open(48));
        int fragile = sim.RegisterDesign(new UnitDesign { Hp = 60, Damage = 8, SpeedStat = 6, RangeStat = 3, Cooldown = 12 });
        int tanky = sim.RegisterDesign(new UnitDesign { Hp = 150, Damage = 8, SpeedStat = 3, RangeStat = 3, Cooldown = 15 });

        var atkA = sim.SpawnUnit(1, 20, 20);        // default attacker
        var glass = sim.SpawnUnit(2, 21, 20, fragile);
        var atkB = sim.SpawnUnit(1, 20, 30);        // default attacker
        var wall = sim.SpawnUnit(2, 21, 30, tanky);

        Order(sim, Attack(atkA, glass));
        Order(sim, Attack(atkB, wall));

        int glassDied = -1, wallDied = -1;
        for (int i = 0; i < 600; i++)
        {
            sim.Tick(Array.Empty<Command>());
            if (glassDied < 0 && sim.Units.Find(u => u.Id == glass.Id) == null) glassDied = sim.TickNumber;
            if (wallDied < 0 && sim.Units.Find(u => u.Id == wall.Id) == null) wallDied = sim.TickNumber;
        }
        Check($"the fragile unit died first (glass @ {glassDied}, tank @ {wallDied})",
              glassDied > 0 && (wallDied < 0 || glassDied < wallDied));
    }

    static void HighDamageKillsFaster()
    {
        Console.WriteLine("\na high-damage design kills faster:");
        // Same cooldown as the default soldier, damage traded up by cutting HP —
        // so kill SPEED isolates the damage stat. Targets do not fight back.
        var sim = new Simulation(TileMap.Open(48));
        int sharp = sim.RegisterDesign(new UnitDesign { Hp = 40, Damage = 14, SpeedStat = 3, RangeStat = 3, Cooldown = 10 });
        Check("the high-damage design fits the budget", sharp == 1);

        var sharpshooter = sim.SpawnUnit(1, 20, 20, sharp);
        var dummyA = sim.SpawnUnit(2, 21, 20);      // default 100 HP, does not attack
        var grunt = sim.SpawnUnit(1, 20, 30);       // default attacker
        var dummyB = sim.SpawnUnit(2, 21, 30);      // default 100 HP

        Order(sim, Attack(sharpshooter, dummyA));
        Order(sim, Attack(grunt, dummyB));

        int sharpKill = -1, gruntKill = -1;
        for (int i = 0; i < 800; i++)
        {
            sim.Tick(Array.Empty<Command>());
            if (sharpKill < 0 && sim.Units.Find(u => u.Id == dummyA.Id) == null) sharpKill = sim.TickNumber;
            if (gruntKill < 0 && sim.Units.Find(u => u.Id == dummyB.Id) == null) gruntKill = sim.TickNumber;
        }
        Check("both dummies died", sharpKill > 0 && gruntKill > 0);
        Check($"the high-damage unit killed faster (sharp @ {sharpKill}, default @ {gruntKill})",
              sharpKill < gruntKill);
    }

    static void ARangedDesignHitsFromAfar()
    {
        Console.WriteLine("\na ranged design attacks without closing to melee:");
        var sim = new Simulation(TileMap.Open(48));
        // RangeStat 8 == 4 tiles of reach.
        int archer = sim.RegisterDesign(new UnitDesign { Hp = 55, Damage = 9, SpeedStat = 6, RangeStat = 8, Cooldown = 12 });
        Check("the archer fits the budget", archer == 1);

        var shooter = sim.SpawnUnit(1, 20, 20, archer);
        var dummy = sim.SpawnUnit(2, 24, 20);        // 4 tiles away, does not fight back
        int startX = shooter.X;

        sim.Tick(new List<Command>
        {
            new Command { Owner = 1, Type = CommandType.Attack, UnitIds = new[] { shooter.Id }, TargetId = dummy.Id },
        });
        for (int i = 0; i < 60; i++) sim.Tick(Array.Empty<Command>());

        Check("the target is taking damage", dummy.Hp < 100);
        Check("the archer stayed put (never closed to melee)", shooter.X == startX);
        int gap = Fixed.VLen(dummy.X - shooter.X, dummy.Y - shooter.Y);
        Check($"and it is firing from beyond melee reach ({gap / (double)Fixed.One:0.#} tiles)",
              gap > Fixed.One * 3 / 2);

        // Contrast: a default (melee) unit must close the distance to fight.
        var melee = sim.SpawnUnit(1, 30, 30);
        var dummy2 = sim.SpawnUnit(2, 34, 30);
        int meleeStartX = melee.X;
        sim.Tick(new List<Command>
        {
            new Command { Owner = 1, Type = CommandType.Attack, UnitIds = new[] { melee.Id }, TargetId = dummy2.Id },
        });
        for (int i = 0; i < 60; i++) sim.Tick(Array.Empty<Command>());
        Check("a melee unit, by contrast, moved in to attack", melee.X > meleeStartX);
    }

    static void TwoClientsAgreeWithMixedDesigns()
    {
        Console.WriteLine("\ntwo clients agree with mixed designs:");
        var net = new LoopbackTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a);
        net.Connect(b);

        // Both register the SAME roster in the SAME order, then field a mix.
        foreach (var c in new[] { a, b })
        {
            c.Sim.RegisterDesign(new UnitDesign { Hp = 60, Damage = 8, SpeedStat = 10, RangeStat = 3, Cooldown = 12 }); // 1: runner
            c.Sim.RegisterDesign(new UnitDesign { Hp = 150, Damage = 11, SpeedStat = 3, RangeStat = 3, Cooldown = 15 }); // 2: brute
            c.Sim.SpawnUnit(1, 20, 20, 1);
            c.Sim.SpawnUnit(1, 20, 22, 0);
            c.Sim.SpawnUnit(2, 24, 20, 2);
            c.Sim.SpawnUnit(2, 24, 22, 1);
        }

        var script = new Dictionary<int, Action>
        {
            [1] = () =>
            {
                a.Issue(AttackIds(1, 3)); b.Issue(AttackIds(1, 3));
                a.Issue(AttackIds(2, 4)); b.Issue(AttackIds(2, 4));
                a.Issue(AttackIds(3, 1)); b.Issue(AttackIds(3, 1));
                a.Issue(AttackIds(4, 2)); b.Issue(AttackIds(4, 2));
            },
        };

        int desyncs = 0, first = -1;
        for (int t = 0; t < 600; t++)
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
        Check($"StateChecksum identical on all 600 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check("both agree on the survivors", a.Sim.Units.Count == b.Sim.Units.Count);
        Check("both agree on the winner", a.Sim.MatchWinner() == b.Sim.MatchWinner());
    }

    static void DesignsSurviveARejoin()
    {
        Console.WriteLine("\na rejoin carries the design roster:");
        var host = new Simulation(TileMap.Open(48));
        int brute = host.RegisterDesign(new UnitDesign { Hp = 150, Damage = 11, SpeedStat = 3, RangeStat = 3, Cooldown = 15 });
        host.SpawnUnit(1, 20, 20, brute);
        host.SpawnUnit(2, 21, 20);
        Order(host, AttackIdsOn(host, 2, 1));       // default soldier attacks the brute
        for (int i = 0; i < 60; i++) host.Tick(Array.Empty<Command>());

        var rejoiner = new Simulation(TileMap.Open(48));
        var units = new List<Unit>();
        foreach (var u in host.Units) units.Add(u.Clone());
        rejoiner.Restore(host.TickNumber, host.NextUnitId, host.RngState, units,
                         host.NextNodeId, host.NodeList, host.Stockpiles, host.DropOffs,
                         host.NextBuildingId, host.BuildingList, host.Designs);

        Check("the roster came across", rejoiner.Designs.Count == host.Designs.Count);
        Check("the brute's stats came across",
              rejoiner.DesignOf(brute).Hp == 150 && rejoiner.DesignOf(brute).Damage == 11);
        Check("the rebuilt sim hashes identically at the join",
              rejoiner.StateChecksum() == host.StateChecksum());

        int desyncs = 0;
        for (int i = 0; i < 300; i++)
        {
            host.Tick(Array.Empty<Command>());
            rejoiner.Tick(Array.Empty<Command>());
            if (host.StateChecksum() != rejoiner.StateChecksum()) desyncs++;
        }
        Check("no divergence over 300 ticks after the rejoin", desyncs == 0);
    }

    // ---- helpers -----------------------------------------------------------

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

    static Command Move(Unit u, int x, int y) => new Command
    { Owner = u.Owner, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = x, Y = y };

    static Command Attack(Unit u, Unit target) => new Command
    { Owner = u.Owner, Type = CommandType.Attack, UnitIds = new[] { u.Id }, TargetId = target.Id };

    static Command AttackIds(int unit, int target) => new Command
    { Type = CommandType.Attack, UnitIds = new[] { unit }, TargetId = target };

    // Direct-sim attack by id, reading the owner off the unit.
    static Command AttackIdsOn(Simulation sim, int unit, int target)
    {
        var u = sim.Units.Find(v => v.Id == unit);
        return new Command { Owner = u.Owner, Type = CommandType.Attack, UnitIds = new[] { unit }, TargetId = target };
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
