// Dragon — a flying, fire-breathing legend, its air-only combat, and its counters.

using System;
using System.Collections.Generic;
using Sim;
using Netcode;

static class Program
{
    static int _failures;

    // Design ids after registering the Skirmish roster.
    const int Soldier = 0, Champion = 9, Dragon = 11;

    static void Main()
    {
        Console.WriteLine("Dragon — flight, fire, and the harpoon\n");

        ADragonFliesStraightOverAnything();
        GroundTroopsCannotTouchADragon();
        ADragonScorchesTroopsAndBuildings();
        DragonsCatchEachOtherOut();
        AHarpoonTowerShootsADragonDown();
        AHarpoonTowerIgnoresFootsoldiers();
        ADragonIsMusteredForPrestige();
        MusteringNeverConfusesDragonAndChampion();
        TwoClientsAgreeWithDragonsAndHarpoons();
        ADragonSurvivesTheWire();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // A Dragon ignores the ground entirely: ordered across a solid rock wall that no
    // ground unit can path around, it beelines straight over and lands on the far side.
    static void ADragonFliesStraightOverAnything()
    {
        Console.WriteLine("a dragon flies straight over any terrain:");
        // A solid vertical rock wall at column 6 splits the map in two — no way round.
        var sim = new Simulation(TileMap.FromRows(
            "......#......",
            "......#......",
            "......#......",
            "......#......",
            "......#......"));
        Reg(sim);

        var dragon = sim.SpawnUnit(1, 2, 2, Dragon);
        Order(sim, Move(dragon, 10, 2));
        Check("the dragon takes a straight flight — no ground route (A* path is null)", !dragon.HasPath);
        for (int i = 0; i < 300 && Fixed.ToInt(dragon.X) < 10; i++) sim.Tick(Array.Empty<Command>());
        Check($"it crossed the rock wall to the far side (x={Fixed.ToInt(dragon.X)})", Fixed.ToInt(dragon.X) >= 10);

        // A ground soldier can't even be routed across — the wall seals it off.
        var soldier = sim.SpawnUnit(1, 2, 3, Soldier);
        Order(sim, Move(soldier, 10, 3));
        for (int i = 0; i < 300; i++) sim.Tick(Array.Empty<Command>());
        Check($"a footsoldier is walled off on the near side (x={Fixed.ToInt(soldier.X)})", Fixed.ToInt(soldier.X) < 6);
    }

    // A flyer is beyond a ground fighter's reach: it can be neither ordered to attack
    // the dragon nor auto-acquire it, so a lone soldier stands helpless beneath it.
    static void GroundTroopsCannotTouchADragon()
    {
        Console.WriteLine("\nground troops cannot touch a dragon overhead:");
        var sim = new Simulation(TileMap.Open(24));
        Reg(sim);
        var dragon  = sim.SpawnUnit(1, 10, 10, Dragon);
        var soldier = sim.SpawnUnit(2, 11, 10, Soldier);   // enemy, right beside it

        int hp0 = dragon.Hp;
        Order(sim, Attack(soldier, dragon));
        Check("a footsoldier can't be ordered to engage a flyer", soldier.TargetId == 0);
        for (int i = 0; i < 200; i++) sim.Tick(Array.Empty<Command>());
        Check("the dragon takes no damage from the ground", dragon.Hp == hp0);
        Check("and the soldier never picks it up as a target", soldier.TargetId == 0);
    }

    // The dragon's fire scorches troops (its Damage) AND batters buildings from range
    // (its SiegeDamage) — flying over the wall to burn what's behind it.
    static void ADragonScorchesTroopsAndBuildings()
    {
        Console.WriteLine("\na dragon scorches troops and buildings:");
        var sim = new Simulation(TileMap.Open(32));
        Reg(sim);
        var dragon = sim.SpawnUnit(1, 10, 10, Dragon);
        var prey   = sim.SpawnUnit(2, 13, 10, Soldier);
        Order(sim, Attack(dragon, prey));
        Check("the dragon takes the target (a flyer can strike the ground)", dragon.TargetId == prey.Id);
        for (int i = 0; i < 300 && prey.Alive; i++) sim.Tick(Array.Empty<Command>());
        Check("its fire cuts the soldier down", !prey.Alive);

        var sim2 = new Simulation(TileMap.Open(32));
        Reg(sim2);
        var d2 = sim2.SpawnUnit(1, 10, 10, Dragon);
        var wall = sim2.PlaceBuilding(BuildingType.Wall, 2, 14, 10);
        int wallHp = wall.Hp;
        Order(sim2, AttackBuilding(new[] { d2 }, wall));
        for (int i = 0; i < 300 && wall.Alive; i++) sim2.Tick(Array.Empty<Command>());
        Check($"and its fire batters buildings too (wall {wallHp} → {(wall.Alive ? wall.Hp : 0)})", !wall.Alive || wall.Hp < wallHp);
    }

    // "Dragons can catch each other out": a flyer CAN target a flyer, so a dragon
    // duel resolves — the only mobile answer to a dragon is another dragon.
    static void DragonsCatchEachOtherOut()
    {
        Console.WriteLine("\ndragons catch each other out:");
        var sim = new Simulation(TileMap.Open(24));
        Reg(sim);
        var mine  = sim.SpawnUnit(1, 10, 10, Dragon);
        var yours = sim.SpawnUnit(2, 12, 10, Dragon);
        Order(sim, Attack(mine, yours));
        Order(sim, Attack(yours, mine));
        Check("a dragon CAN be ordered onto another dragon", mine.TargetId == yours.Id && yours.TargetId == mine.Id);
        for (int i = 0; i < 1200 && mine.Alive && yours.Alive; i++) sim.Tick(Array.Empty<Command>());
        Check("the duel resolves — one dragon falls", !mine.Alive || !yours.Alive);
    }

    // The Harpoon Tower is the fixed answer: a square flat tower that fires on its own
    // at any enemy flyer overhead, and brings it down.
    static void AHarpoonTowerShootsADragonDown()
    {
        Console.WriteLine("\na harpoon tower shoots a dragon down:");
        var sim = new Simulation(TileMap.Open(32));
        Reg(sim);
        var tower  = sim.PlaceBuilding(BuildingType.HarpoonTower, 1, 15, 15);
        var dragon = sim.SpawnUnit(2, 16, 16, Dragon);   // an enemy dragon right over the battery

        int hp0 = dragon.Hp;
        int fellAt = -1;
        for (int i = 0; i < 800 && dragon.Alive; i++)
        {
            sim.Tick(Array.Empty<Command>());
            if (fellAt < 0 && !dragon.Alive) fellAt = sim.TickNumber;
        }
        Check($"the harpoon chewed the dragon down (start {hp0} hp)", hp0 == 520);
        Check($"and finally shot it out of the sky (~{fellAt} ticks)", !dragon.Alive);
        Check("the tower still stands (a dragon out of reach never scratched it)", tower.Alive);
    }

    // The harpoon aims only at the sky: a ground soldier walks under it untouched.
    static void AHarpoonTowerIgnoresFootsoldiers()
    {
        Console.WriteLine("\na harpoon tower ignores footsoldiers:");
        var sim = new Simulation(TileMap.Open(32));
        Reg(sim);
        sim.PlaceBuilding(BuildingType.HarpoonTower, 1, 15, 15);
        var soldier = sim.SpawnUnit(2, 16, 16, Soldier);   // an enemy on the ground, point-blank

        int hp0 = soldier.Hp;
        for (int i = 0; i < 300; i++) sim.Tick(Array.Empty<Command>());
        Check("a ground soldier beneath the tower is never fired on", soldier.Alive && soldier.Hp == hp0);
    }

    // A Dragon is mustered for a king's ransom in Prestige at a Royal Kitchen — never
    // trained, never point-bought — and it is a flying, fire-breathing legend.
    static void ADragonIsMusteredForPrestige()
    {
        Console.WriteLine("\na dragon is mustered for prestige:");
        var sim = new Simulation(TileMap.Open(48));
        Reg(sim);
        sim.PlaceBuilding(BuildingType.RoyalKitchen, 1, 20, 20);
        Check("cannot muster with no prestige", !sim.CanMusterDragon(1));

        sim.AddResource(1, ResourceType.Food, 6000);   // feast up the renown
        sim.AddGold(1, 4000);                          // and hold tournaments to speed it
        for (int i = 0; i < 40000 && sim.Prestige(1) < Simulation.DragonCost; i++) sim.Tick(Array.Empty<Command>());
        Check($"the court amassed a dragon's ransom in prestige ({sim.Prestige(1)})", sim.Prestige(1) >= Simulation.DragonCost);
        Check("and now it can muster the legend", sim.CanMusterDragon(1));

        int units = sim.Units.Count, p = sim.Prestige(1);
        Order(sim, new Command { Owner = 1, Seq = 1, Type = CommandType.MusterDragon });
        Check("a dragon appeared", sim.Units.Count == units + 1);
        Check($"prestige was spent ({p} → {sim.Prestige(1)})", sim.Prestige(1) == p - Simulation.DragonCost);
        var d = sim.Units[sim.Units.Count - 1];
        Check($"it is the flying legend ({d.MaxHp} hp, design {d.DesignId})",
              d.DesignId == Dragon && sim.DesignOf(d.DesignId).Flying && d.MaxHp >= 500);
    }

    // The Dragon and the Champion are both non-trainable legends mustered at the court,
    // but they are never mixed up: M raises a Champion, Shift+M a Dragon.
    static void MusteringNeverConfusesDragonAndChampion()
    {
        Console.WriteLine("\nmustering never confuses dragon and champion:");
        var sim = new Simulation(TileMap.Open(32));
        Reg(sim);
        sim.PlaceBuilding(BuildingType.RoyalKitchen, 1, 15, 15);
        sim.AddResource(1, ResourceType.Food, 8000);
        sim.AddGold(1, 6000);
        for (int i = 0; i < 60000 && sim.Prestige(1) < Simulation.DragonCost + Simulation.ChampionCost; i++)
            sim.Tick(Array.Empty<Command>());

        Order(sim, new Command { Owner = 1, Seq = 1, Type = CommandType.MusterChampion });
        var champ = sim.Units[sim.Units.Count - 1];
        Check($"M raises a Champion, not a dragon (design {champ.DesignId})",
              champ.DesignId == Champion && !sim.DesignOf(champ.DesignId).Flying);

        Order(sim, new Command { Owner = 1, Seq = 2, Type = CommandType.MusterDragon });
        var drag = sim.Units[sim.Units.Count - 1];
        Check($"Shift+M raises a Dragon, not a champion (design {drag.DesignId})",
              drag.DesignId == Dragon && sim.DesignOf(drag.DesignId).Flying);
    }

    // Everything above must be byte-identical on every machine: two clients fly dragons
    // over a harpoon battery and never disagree on a single tick's StateChecksum.
    static void TwoClientsAgreeWithDragonsAndHarpoons()
    {
        Console.WriteLine("\ntwo clients agree with dragons and harpoons in play:");
        var net = new LoopbackTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            Reg(c.Sim);
            c.Sim.PlaceBuilding(BuildingType.HarpoonTower, 1, 20, 20);   // building id 1
            c.Sim.SpawnUnit(2, 30, 20, Dragon);                          // unit id 1 — enemy dragon
            c.Sim.SpawnUnit(2, 30, 22, Dragon);                          // unit id 2
        }

        var script = new Dictionary<int, Action>
        {
            // Fly both enemy dragons in over the battery.
            [1] = () => { b.Issue(Move(1, 18, 20)); b.Issue(Move(2, 18, 22)); },
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
    }

    // A rejoin must carry the new Flying design flag and a harpoon tower mid-reload
    // across the wire — Snapshot → Serialize → Deserialize → Restore hashes identically.
    static void ADragonSurvivesTheWire()
    {
        Console.WriteLine("\na dragon (and a reloading harpoon) survive the wire:");
        var host = new Simulation(TileMap.Open(32));
        Reg(host);
        host.PlaceBuilding(BuildingType.HarpoonTower, 1, 15, 15);
        host.SpawnUnit(2, 16, 16, Dragon);
        // Run a while so the harpoon is mid-reload and the dragon is damaged & moving.
        for (int i = 0; i < 40; i++) host.Tick(Array.Empty<Command>());

        var bytes = Wire.Serialize(host.Snapshot());
        var snap = Wire.DeserializeSnapshot(bytes);
        var rejoiner = new Simulation(TileMap.Open(32));
        rejoiner.Restore(snap);

        Check("the rebuilt sim hashes identically at the join",
              rejoiner.StateChecksum() == host.StateChecksum());
        Check("the Dragon design came across flying",
              rejoiner.DesignOf(Dragon).Flying && rejoiner.DesignOf(Dragon).SiegeDamage > 0);

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

    static void Reg(Simulation sim) { foreach (var d in Skirmish.Designs()) sim.RegisterDesign(d); }

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

    static Command Move(Unit u, int x, int y) => new Command
    { Owner = u.Owner, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = x, Y = y };
    static Command Move(int unitId, int x, int y) => new Command
    { Owner = 2, Type = CommandType.Move, UnitIds = new[] { unitId }, X = x, Y = y };

    static Command Attack(Unit u, Unit target) => new Command
    { Owner = u.Owner, Type = CommandType.Attack, UnitIds = new[] { u.Id }, TargetId = target.Id };

    static Command AttackBuilding(Unit[] us, Building b)
    {
        var ids = new int[us.Length];
        for (int i = 0; i < us.Length; i++) ids[i] = us[i].Id;
        return new Command { Owner = us[0].Owner, Type = CommandType.AttackBuilding, UnitIds = ids, TargetId = b.Id };
    }

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "OK" : "XX")}] {what}");
        if (!ok) _failures++;
    }
}
