// Fire — dragon-breath ignites buildings; Wells douse them.

using System;
using System.Collections.Generic;
using Sim;
using Netcode;

static class Program
{
    static int _failures;
    const int Dragon = 11;

    static void Main()
    {
        Console.WriteLine("Fire — dragon-breath & the well\n");

        DragonFireIgnitesABuilding();
        AnUndousedFireEatsTheBuilding();
        AWellDousesTheFire();
        FireSpreadsToANeighbour();
        AWellShieldsTheNeighbour();
        TwoClientsAgreeWithFireAndWells();
        BurningSurvivesTheWire();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // A dragon's breath doesn't just batter a building — it sets it ablaze.
    static void DragonFireIgnitesABuilding()
    {
        Console.WriteLine("a dragon sets a building ablaze:");
        var sim = new Simulation(TileMap.Open(40));
        Reg(sim);
        var keep = sim.PlaceBuilding(BuildingType.Keep, 2, 22, 18);
        var dragon = sim.SpawnUnit(1, 16, 19, Dragon);
        Order(sim, AttackBuilding(dragon, keep));
        for (int i = 0; i < 400 && keep.Alive && keep.Burning == 0; i++) sim.Tick(Array.Empty<Command>());
        Check($"the dragon's fire caught (Burning={keep.Burning})", keep.Burning > 0);
    }

    // An un-doused blaze burns for a while, eating the structure, then dies down.
    static void AnUndousedFireEatsTheBuilding()
    {
        Console.WriteLine("\nan un-doused fire eats the building:");
        var sim = new Simulation(TileMap.Open(24));
        var keep = sim.PlaceBuilding(BuildingType.Keep, 1, 10, 10);   // 600 hp — survives one blaze
        int hp0 = keep.Hp;
        keep.Burning = 160;
        for (int i = 0; i < 400 && keep.Burning > 0; i++) sim.Tick(Array.Empty<Command>());
        Check($"the fire ate its hit points ({hp0} → {keep.Hp})", keep.Hp < hp0 - 100);
        Check("and burned itself out", keep.Burning == 0);

        // A slighter building is razed outright by the flames.
        var sim2 = new Simulation(TileMap.Open(24));
        var house = sim2.PlaceBuilding(BuildingType.House, 1, 10, 10);   // 160 hp
        house.Burning = 160;
        for (int i = 0; i < 400 && house.Alive; i++) sim2.Tick(Array.Empty<Command>());
        Check("a house burns to the ground", !house.Alive);
    }

    // A Well within reach fights the blaze down — fast, and with no structural loss.
    static void AWellDousesTheFire()
    {
        Console.WriteLine("\na well douses the fire:");
        var sim = new Simulation(TileMap.Open(24));
        var house = sim.PlaceBuilding(BuildingType.House, 1, 10, 10);
        sim.PlaceBuilding(BuildingType.Well, 1, 13, 10);   // within reach (7 tiles)
        int hp0 = house.Hp;
        house.Burning = 160;
        int outAt = -1;
        for (int i = 0; i < 200; i++)
        {
            sim.Tick(Array.Empty<Command>());
            if (outAt < 0 && house.Burning == 0) outAt = sim.TickNumber;
        }
        Check($"the well put the fire out fast (~{outAt} ticks)", outAt > 0 && outAt < 60);
        Check("and the building took no fire damage", house.Alive && house.Hp == hp0);
    }

    // Left alone, a blaze jumps to the nearest un-burnt friendly building nearby.
    static void FireSpreadsToANeighbour()
    {
        Console.WriteLine("\nfire spreads to a neighbour:");
        var sim = new Simulation(TileMap.Open(32));
        var b1 = sim.PlaceBuilding(BuildingType.Keep, 1, 10, 10);   // tough, so it burns long enough to spread
        var b2 = sim.PlaceBuilding(BuildingType.House, 1, 14, 11);  // close (within 3 tiles of the keep's edge)
        b1.Burning = 900;
        for (int i = 0; i < 500 && b2.Burning == 0; i++) sim.Tick(Array.Empty<Command>());
        Check($"the blaze jumped to the neighbour (Burning={b2.Burning})", b2.Burning > 0);
    }

    // ...unless a Well protects that neighbour — the fire cannot take hold there.
    static void AWellShieldsTheNeighbour()
    {
        Console.WriteLine("\na well shields the neighbour from catching:");
        var sim = new Simulation(TileMap.Open(32));
        var b1 = sim.PlaceBuilding(BuildingType.Keep, 1, 10, 10);
        var b2 = sim.PlaceBuilding(BuildingType.House, 1, 14, 11);
        var well = sim.PlaceBuilding(BuildingType.Well, 1, 16, 11);   // clear of the house, still covers it
        Check("(the well was placed)", well != null);
        b1.Burning = 900;
        for (int i = 0; i < 500; i++) sim.Tick(Array.Empty<Command>());
        Check("the well-protected neighbour never caught", b2.Burning == 0 && b2.Alive);
    }

    // All of it byte-identical on every machine: a dragon burning an enemy keep, with a
    // well fighting a fire of its own, never desyncs.
    static void TwoClientsAgreeWithFireAndWells()
    {
        Console.WriteLine("\ntwo clients agree with fire & wells in play:");
        var net = new LoopbackTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a); net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            Reg(c.Sim);
            c.Sim.PlaceBuilding(BuildingType.Keep, 2, 26, 20);   // enemy keep (building id 1) — burns un-doused
            c.Sim.PlaceBuilding(BuildingType.Well, 2, 18, 18);   // id 2 — far from the keep
            var house = c.Sim.PlaceBuilding(BuildingType.House, 2, 18, 21); // id 3 — under the well
            house.Burning = 160;                                 // a fire the well is already fighting
            c.Sim.SpawnUnit(1, 22, 21, Dragon);                  // our dragon (unit id 1)
        }

        var script = new Dictionary<int, Action>
        {
            [1] = () => { a.Issue(AttackBuildingIds(new[] { 1 }, 1)); b.Issue(AttackBuildingIds(new[] { 1 }, 1)); },
        };

        int desyncs = 0, first = -1;
        for (int t = 0; t < 500; t++)
        {
            if (script.TryGetValue(t, out var act)) act();
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical on all 500 ticks" + (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
    }

    // A rejoin carries a building's fire across the wire.
    static void BurningSurvivesTheWire()
    {
        Console.WriteLine("\na building's fire survives the wire:");
        var host = new Simulation(TileMap.Open(24));
        var keep = host.PlaceBuilding(BuildingType.Keep, 1, 10, 10);
        keep.Burning = 130;
        for (int i = 0; i < 25; i++) host.Tick(Array.Empty<Command>());   // mid-burn

        var snap = Wire.DeserializeSnapshot(Wire.Serialize(host.Snapshot()));
        var rejoiner = new Simulation(TileMap.Open(24));
        rejoiner.Restore(snap);
        Check("the rebuilt sim hashes identically at the join", rejoiner.StateChecksum() == host.StateChecksum());
        Check("the blaze came across", rejoiner.BuildingList[0].Burning == keep.Burning && keep.Burning > 0);

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

    static Command AttackBuilding(Unit u, Building bld) => new Command
    { Owner = u.Owner, Type = CommandType.AttackBuilding, UnitIds = new[] { u.Id }, TargetId = bld.Id };
    static Command AttackBuildingIds(int[] unitIds, int buildingId) => new Command
    { Owner = 1, Type = CommandType.AttackBuilding, UnitIds = unitIds, TargetId = buildingId };

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "OK" : "XX")}] {what}");
        if (!ok) _failures++;
    }
}
