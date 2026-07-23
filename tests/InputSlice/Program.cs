// InputSlice — drives the vertical slice the way a player's mouse would, headlessly.
//
// Mirrors Main.cs exactly: same two loopback clients, same starting armies, same
// screen<->world conversion, same "left-drag selects, right-click moves" flow.
// The only thing missing is Godot drawing it. Asserts, every single tick, that
// the two clients' checksums agree — a desync anywhere in the input path fails.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    // These three constants are the contract with Main.cs. If you change them
    // there, change them here — the whole point is to exercise the same numbers.
    const int TicksPerSecond = 20;
    const float PxPerUnit = 12f;
    const int MyPlayer = 1;

    static int _failures;

    static void Main()
    {
        Console.WriteLine("InputSlice — mouse-driven vertical slice, no window\n");

        var net = new LoopbackTransport();
        var me = new Client(1, net);
        var other = new Client(2, net);
        net.Connect(me);
        net.Connect(other);

        foreach (var c in new[] { me, other })
        {
            c.Sim.SpawnUnit(1, 8, 8);
            c.Sim.SpawnUnit(1, 11, 8);
            c.Sim.SpawnUnit(1, 8, 11);
            c.Sim.SpawnUnit(2, 44, 40);
            c.Sim.SpawnUnit(2, 47, 40);
        }
        Check("both clients start identical", me.Sim.Checksum() == other.Sim.Checksum());

        // --- The mouse gesture: left-drag a box around player 1's three units.
        // Screen pixels, as Godot would report them from the window's top-left.
        var selected = SelectInBox(me, 60f, 60f, 150f, 150f);
        Check($"box-select caught all 3 player-1 units (got {selected.Count})",
              selected.Count == 3);
        Check("box-select caught no enemy units",
              !selected.Contains(4) && !selected.Contains(5));

        // --- Right-click at screen (400, 300) => world (33, 25).
        int tx = (int)MathF.Round(400f / PxPerUnit);
        int ty = (int)MathF.Round(300f / PxPerUnit);
        Check($"right-click screen (400,300) maps to world ({tx},{ty})", tx == 33 && ty == 25);

        me.Issue(new Command
        {
            Type = CommandType.Move,
            UnitIds = selected.ToArray(),
            X = tx,
            Y = ty,
        });

        // --- Run 20 seconds of game time, checking sync on EVERY tick.
        // Long enough for the arrival check below: the farthest unit travels
        // ~30.2 world units at 1/8 per tick = ~242 ticks, plus 3 of input delay.
        const int ticks = TicksPerSecond * 20;
        int desyncs = 0, firstDesync = -1;
        for (int t = 0; t < ticks; t++)
        {
            // Publish both clients' turns before either consumes one — see
            // Client.SendInput.
            me.SendInput();
            other.SendInput();
            me.TryStep();
            other.TryStep();
            if (me.Sim.Checksum() != other.Sim.Checksum())
            {
                if (firstDesync < 0) firstDesync = t;
                desyncs++;
            }
        }
        Check($"in sync on all {ticks} ticks" +
              (desyncs > 0 ? $" (desynced {desyncs}x, first at tick {firstDesync})" : ""),
              desyncs == 0);

        // --- The commanded units actually went where the click said.
        foreach (int id in selected)
        {
            var u = me.Sim.Units.Find(v => v.Id == id);
            bool arrived = u.X == Fixed.FromInt(tx) && u.Y == Fixed.FromInt(ty);
            Check($"unit {id} arrived at ({tx},{ty}) — now " +
                  $"({u.X / (double)Fixed.One:0.###}, {u.Y / (double)Fixed.One:0.###})", arrived);
        }

        // --- Units NOT selected must not have budged. Command scoping matters:
        // player 2's units are in the same sim and must ignore player 1's order.
        foreach (int id in new[] { 4, 5 })
        {
            var u = me.Sim.Units.Find(v => v.Id == id);
            Check($"enemy unit {id} did not move", u.X == u.Tx && u.Y == u.Ty &&
                  u.X == Fixed.FromInt(id == 4 ? 44 : 47));
        }

        Console.WriteLine();
        Console.WriteLine($"final checksum 0x{me.Sim.Checksum():X8}  (tick {me.Sim.TickNumber})");
        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // Same logic as Main.SelectInBox: screen-space rect test over player-1 units.
    static List<int> SelectInBox(Client c, float x0, float y0, float x1, float y1)
    {
        float lo_x = MathF.Min(x0, x1), hi_x = MathF.Max(x0, x1);
        float lo_y = MathF.Min(y0, y1), hi_y = MathF.Max(y0, y1);
        var hits = new List<int>();
        foreach (var u in c.Sim.Units)
        {
            if (u.Owner != MyPlayer) continue;
            float sx = u.X / (float)Fixed.One * PxPerUnit;
            float sy = u.Y / (float)Fixed.One * PxPerUnit;
            if (sx >= lo_x && sx <= hi_x && sy >= lo_y && sy <= hi_y) hits.Add(u.Id);
        }
        return hits;
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
