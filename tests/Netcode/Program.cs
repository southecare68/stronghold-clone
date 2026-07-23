// Netcode — wire format, join codes, stalling, and desync detection.
//
// These are the failure modes that only show up between two machines, which is
// the most expensive place to debug them. Each one is reproduced here in a
// single process instead.

using System;
using System.Collections.Generic;
using Sim;
using Netcode;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Netcode — protocol tests (no Godot, no socket)\n");

        WireRoundTrip();
        WireRejectsGarbage();
        MatchCodeRoundTrip();
        ClientStallsUntilPeerSpeaks();
        DesyncIsReported();
        AgreementIsNotReportedAsDesync();
        SnapshotRoundTrip();
        RejoinResumesTheMatch();
        CorruptSnapshotIsCaughtOnArrival();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // Every field must survive the round trip. Seq especially: losing it puts
    // command ordering back on arrival order — see tests/CommandOrder.
    static void WireRoundTrip()
    {
        Console.WriteLine("wire format:");
        var turn = new TurnInput
        {
            Owner = 2,
            Tick = 12345,
            ChecksumTick = 12342,
            Checksum = 0xDEADBEEF,
            Commands = new[]
            {
                new Command
                {
                    Owner = 2, Type = CommandType.Move, Seq = 77,
                    X = -30, Y = 25, ExecTick = 12345,
                    UnitIds = new[] { 3, 4, 5 },
                },
                new Command
                {
                    Owner = 2, Type = CommandType.Move, Seq = 78,
                    X = int.MaxValue, Y = int.MinValue, ExecTick = 12345,
                    UnitIds = Array.Empty<int>(),
                },
            },
        };

        var back = Wire.Deserialize(Wire.Serialize(turn));
        Check("a turn survives serialization", back != null);
        if (back == null) return;

        Check("owner / tick / checksum preserved",
              back.Owner == turn.Owner && back.Tick == turn.Tick &&
              back.ChecksumTick == turn.ChecksumTick && back.Checksum == turn.Checksum);
        Check("command count preserved", back.Commands.Length == 2);

        var a = turn.Commands[0];
        var b = back.Commands[0];
        Check("Seq preserved (losing it reintroduces the ordering desync)", b.Seq == a.Seq);
        Check("owner / type / x / y / execTick preserved",
              b.Owner == a.Owner && b.Type == a.Type && b.X == a.X &&
              b.Y == a.Y && b.ExecTick == a.ExecTick);
        Check("unit ids preserved in order",
              b.UnitIds.Length == 3 && b.UnitIds[0] == 3 && b.UnitIds[1] == 4 && b.UnitIds[2] == 5);
        Check("extreme coordinates survive intact",
              back.Commands[1].X == int.MaxValue && back.Commands[1].Y == int.MinValue);
        Check("empty unit list survives", back.Commands[1].UnitIds.Length == 0);

        // Serialization must be a pure function of the turn — two machines
        // building the same turn must produce identical bytes.
        var once = Wire.Serialize(turn);
        var twice = Wire.Serialize(turn);
        bool identical = once.Length == twice.Length;
        for (int i = 0; identical && i < once.Length; i++) identical = once[i] == twice[i];
        Check($"serialization is deterministic ({once.Length} bytes)", identical);
    }

    // A malformed packet must be refused, not half-read. Acting on a partial turn
    // is worse than never receiving it: the stall is at least visible.
    static void WireRejectsGarbage()
    {
        Console.WriteLine("\nwire format rejects bad input:");
        var good = Wire.Serialize(new TurnInput
        {
            Owner = 1, Tick = 4,
            Commands = new[] { new Command { Owner = 1, Seq = 1, UnitIds = new[] { 1 } } },
        });

        Check("null", Wire.Deserialize(null) == null);
        Check("empty", Wire.Deserialize(Array.Empty<byte>()) == null);
        Check("wrong magic", Wire.Deserialize(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) == null);

        var wrongVersion = (byte[])good.Clone();
        wrongVersion[2] = 99;
        Check("wrong protocol version", Wire.Deserialize(wrongVersion) == null);

        var truncated = new byte[good.Length - 3];
        Array.Copy(good, truncated, truncated.Length);
        Check("truncated packet", Wire.Deserialize(truncated) == null);

        var padded = new byte[good.Length + 3];
        Array.Copy(good, padded, good.Length);
        Check("trailing junk", Wire.Deserialize(padded) == null);

        // A count field claiming billions of commands must not be believed.
        // Layout: 4 header, owner, tick, checksumTick, checksum, then the count
        // at offset 20.
        var absurd = (byte[])good.Clone();
        absurd[20] = 0xFF; absurd[21] = 0xFF; absurd[22] = 0xFF; absurd[23] = 0x7F;
        Check("absurd command count", Wire.Deserialize(absurd) == null);

        Check("the good packet still parses", Wire.Deserialize(good) != null);
    }

    static void MatchCodeRoundTrip()
    {
        Console.WriteLine("\nmatch codes:");
        var cases = new[]
        {
            ("192.168.1.42", 27015),
            ("10.0.0.1", 1),
            ("255.255.255.255", 65535),
            ("0.0.0.0", 0),
            ("127.0.0.1", 27015),
        };

        foreach (var (ip, port) in cases)
        {
            string code = MatchCode.Encode(ip, port);
            bool ok = MatchCode.TryDecode(code, out string backIp, out int backPort);
            Check($"{ip}:{port} -> {code} -> {backIp}:{backPort}",
                  ok && backIp == ip && backPort == port);
        }

        // Typing a code back in should be forgiving: case, the dash, and the
        // characters Crockford deliberately avoids.
        string canonical = MatchCode.Encode("192.168.1.42", 27015);
        MatchCode.TryDecode(canonical, out string refIp, out int refPort);
        bool lower = MatchCode.TryDecode(canonical.ToLowerInvariant(), out string ip2, out int p2)
                     && ip2 == refIp && p2 == refPort;
        bool nodash = MatchCode.TryDecode(canonical.Replace("-", ""), out string ip3, out int p3)
                      && ip3 == refIp && p3 == refPort;
        Check("lowercase accepted", lower);
        Check("dash optional", nodash);
        Check("garbage rejected", !MatchCode.TryDecode("hello", out _, out _));
        Check("wrong length rejected", !MatchCode.TryDecode("ABCDE-ABCDEF", out _, out _));
    }

    // The heart of it: a client must not run a tick it does not have every
    // player's input for, no matter how long that takes.
    static void ClientStallsUntilPeerSpeaks()
    {
        Console.WriteLine("\nstalling:");
        var net = new ManualTransport(playerCount: 2);
        var me = new Client(1, net);
        net.Attach(me);
        me.Sim.SpawnUnit(1, 8, 8);

        // We publish our own input, but player 2 has said nothing at all.
        for (int i = 0; i < 5; i++)
        {
            me.SendInput();
            me.TryStep();
        }
        Check("does not advance while a peer is silent", me.Sim.TickNumber == 0);
        Check("reports itself as stalled", me.Stalled);

        // Player 2's opening turns arrive. Now tick 0 can run — and only the
        // ticks we actually have input for.
        for (int t = 0; t <= 2; t++)
            me.Receive(new TurnInput { Owner = 2, Tick = t, ChecksumTick = -1 });

        int advanced = 0;
        for (int i = 0; i < 10; i++)
        {
            me.SendInput();
            if (me.TryStep()) advanced++;
        }
        Check($"advances exactly the 3 ticks it now has input for (ran {advanced})", advanced == 3);
        Check("stalls again at the edge of what it knows", me.Stalled);

        // Feed exactly 18 more ticks of input and it runs exactly 18 more ticks:
        // input available is the only thing rationing progress.
        for (int t = 3; t <= 20; t++)
            me.Receive(new TurnInput { Owner = 2, Tick = t, ChecksumTick = -1 });

        int ran = 0;
        for (int i = 0; i < 18; i++) { me.SendInput(); if (me.TryStep()) ran++; }
        Check($"resumes once input arrives (ran {ran} more, now tick {me.Sim.TickNumber})",
              ran == 18 && me.Sim.TickNumber == 21);
        Check("not stalled while input remains", !me.Stalled);

        // And having consumed everything it was given, it stalls again rather
        // than running a tick on its own authority.
        me.SendInput();
        Check("stalls again at the end of known input", !me.TryStep() && me.Stalled);
    }

    // A peer whose state hash disagrees with ours must be caught immediately,
    // and named — the tick number is what makes a desync debuggable at all.
    static void DesyncIsReported()
    {
        Console.WriteLine("\ndesync detection:");
        var net = new ManualTransport(playerCount: 2);
        var me = new Client(1, net);
        net.Attach(me);
        me.Sim.SpawnUnit(1, 8, 8);

        for (int t = 0; t <= 10; t++)
            me.Receive(new TurnInput { Owner = 2, Tick = t, ChecksumTick = -1 });
        for (int i = 0; i < 5; i++) { me.SendInput(); me.TryStep(); }

        Check("no desync reported while peers agree", me.Desync == null);

        // Player 2 now claims a different world at tick 2.
        me.Receive(new TurnInput
        {
            Owner = 2, Tick = 11, ChecksumTick = 2, Checksum = 0xBADBAD00,
        });

        Check("desync detected", me.Desync != null);
        if (me.Desync == null) return;
        Check($"names the tick it happened on (tick {me.Desync.Tick})", me.Desync.Tick == 2);
        Check($"names the disagreeing player ({me.Desync.RemotePlayer})", me.Desync.RemotePlayer == 2);
        Check("carries both checksums for the log",
              me.Desync.RemoteChecksum == 0xBADBAD00 &&
              me.Desync.LocalChecksum != 0xBADBAD00);
        Console.WriteLine($"        -> \"{me.Desync}\"");
    }

    // False positives would be worse than no detector: a match that reports
    // DESYNC while both sides are fine is unusable.
    static void AgreementIsNotReportedAsDesync()
    {
        Console.WriteLine("\nno false positives:");
        var net = new LoopbackTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.SpawnUnit(1, 8, 8);
            c.Sim.SpawnUnit(2, 44, 40);
        }

        a.Issue(new Command { Type = CommandType.Move, UnitIds = new[] { 1 }, X = 30, Y = 30 });
        b.Issue(new Command { Type = CommandType.Move, UnitIds = new[] { 2 }, X = 12, Y = 12 });

        for (int t = 0; t < 300; t++)
        {
            a.SendInput();
            b.SendInput();
            a.TryStep();
            b.TryStep();
        }
        Check($"300 ticks of real traffic, no desync claimed (tick {a.Sim.TickNumber})",
              a.Desync == null && b.Desync == null && a.Sim.TickNumber == 300);
        Check("and the two sims really do agree", a.Sim.Checksum() == b.Sim.Checksum());
    }

    static void SnapshotRoundTrip()
    {
        Console.WriteLine("\nsnapshot wire format:");
        var (a, b, net) = StartMatch();
        a.Issue(Move(unit: 1, x: 30, y: 30));
        Advance(a, b, net, 40);

        var snap = a.CaptureSnapshot();
        var back = Wire.DeserializeSnapshot(Wire.Serialize(snap));

        Check("a snapshot survives serialization", back != null);
        if (back == null) return;

        Check($"tick preserved ({back.Tick})", back.Tick == snap.Tick);
        Check($"next unit id preserved ({back.NextUnitId})", back.NextUnitId == snap.NextUnitId);
        Check("checksum preserved", back.Checksum == snap.Checksum);
        Check($"all {snap.Units.Length} units preserved", back.Units.Length == snap.Units.Length);

        bool unitsMatch = true;
        for (int i = 0; i < snap.Units.Length && unitsMatch; i++)
        {
            var x = snap.Units[i];
            var y = back.Units[i];
            unitsMatch = x.Id == y.Id && x.Owner == y.Owner && x.X == y.X && x.Y == y.Y &&
                         x.Tx == y.Tx && x.Ty == y.Ty && x.Hp == y.Hp;
        }
        Check("every unit field survives, targets included", unitsMatch);
        Check($"in-flight turns preserved ({back.PendingTurns.Length})",
              back.PendingTurns.Length == snap.PendingTurns.Length);

        // The real test of a snapshot is not that the bytes match — it is that a
        // simulation rebuilt from them hashes identically.
        var fresh = new Client(2, new ManualTransport(2));
        fresh.AdoptSnapshot(back);
        Check($"a sim rebuilt from the bytes hashes identically (0x{snap.Checksum:X8})",
              fresh.Sim.Checksum() == snap.Checksum);
    }

    // The whole point: a player drops out mid-match and comes back to the SAME
    // match, not a new one, and the two stay in sync from there on.
    static void RejoinResumesTheMatch()
    {
        Console.WriteLine("\nrejoin after a disconnect:");
        var (host, peer, net) = StartMatch();

        host.Issue(Move(unit: 1, x: 35, y: 30));
        peer.Issue(Move(unit: 4, x: 20, y: 20));
        Advance(host, peer, net, 60);
        Check($"match running before the drop (tick {host.Sim.TickNumber})",
              host.Sim.TickNumber == 60 && host.Sim.Checksum() == peer.Sim.Checksum());

        // Player 2 vanishes. The host runs out the input it already holds and
        // then stops. It does NOT stop instantly: input delay means the peer had
        // already committed its turns for the next InputDelay ticks before it
        // went away, and those are as good as any other.
        net.Drop(peer);
        int droppedAt = host.Sim.TickNumber;
        for (int i = 0; i < 50; i++) { host.SendInput(); net.Flush(); host.TryStep(); }
        int frozenAt = host.Sim.TickNumber;
        Check($"host runs out the {Client.InputDelay} ticks already committed, then freezes " +
              $"at {frozenAt} — never running on alone",
              frozenAt == droppedAt + Client.InputDelay && host.Stalled);

        // Player 2 comes back as a brand-new client that knows nothing, exactly
        // like a relaunched process: same starting armies, tick 0.
        var rejoiner = new Client(2, net);
        Army(rejoiner);
        Check($"the returning client starts at tick 0, {frozenAt} behind",
              rejoiner.Sim.TickNumber == 0);

        var snap = host.CaptureSnapshot();
        bool adopted = rejoiner.AdoptSnapshot(snap);
        net.Join(rejoiner);

        Check("snapshot adopted and verified against the host's checksum", adopted);
        Check($"rejoiner is now at the host's tick ({rejoiner.Sim.TickNumber})",
              rejoiner.Sim.TickNumber == host.Sim.TickNumber);
        Check("rejoiner's world hashes identically to the host's",
              rejoiner.Sim.Checksum() == host.Sim.Checksum());

        // And the match simply carries on.
        int desyncs = 0;
        for (int i = 0; i < 200; i++)
        {
            host.SendInput();
            rejoiner.SendInput();
            net.Flush();
            host.TryStep();
            rejoiner.TryStep();
            if (host.Sim.Checksum() != rejoiner.Sim.Checksum()) desyncs++;
        }
        Check($"host unfroze and ran on (tick {host.Sim.TickNumber})",
              host.Sim.TickNumber > frozenAt);
        Check($"200 ticks after the rejoin, still in sync every tick", desyncs == 0);
        Check("no desync reported by either side",
              host.Desync == null && rejoiner.Desync == null);

        // Commands still work from the player who rejoined. Checking the TARGET
        // rather than the arrival: the target is what the command carried, and
        // unit 5 has 50 world units to walk, which is 400 ticks away.
        var before = host.Sim.Units.Find(u => u.Id == 5).X;
        rejoiner.Issue(Move(unit: 5, x: 8, y: 8));
        for (int i = 0; i < 200; i++)
        {
            host.SendInput();
            rejoiner.SendInput();
            net.Flush();
            host.TryStep();
            rejoiner.TryStep();
        }
        var onHost = host.Sim.Units.Find(u => u.Id == 5);
        Check("a command issued after rejoining reaches the host and sets the target",
              onHost.Tx == Fixed.FromInt(8) && onHost.Ty == Fixed.FromInt(8));
        Check($"and the unit is actually moving there " +
              $"({onHost.X / (double)Fixed.One:0.##}, {onHost.Y / (double)Fixed.One:0.##})",
              onHost.X < before);
        Check("still in sync afterwards", host.Sim.Checksum() == rejoiner.Sim.Checksum());
    }

    // A snapshot that arrives subtly wrong must be caught at the join, not
    // discovered later as an unexplained desync.
    static void CorruptSnapshotIsCaughtOnArrival()
    {
        Console.WriteLine("\na bad snapshot is caught at the join:");
        var (a, b, net) = StartMatch();
        a.Issue(Move(unit: 1, x: 30, y: 30));
        Advance(a, b, net, 40);

        var snap = a.CaptureSnapshot();
        snap.Units[0].X += 1;                   // one fixed-point unit: 1/65536 of a tile

        var rejoiner = new Client(2, new ManualTransport(2));
        bool ok = rejoiner.AdoptSnapshot(snap);

        Check("adopting reports failure", !ok);
        Check("and records it as a desync", rejoiner.Desync != null);
        if (rejoiner.Desync != null)
            Console.WriteLine($"        -> \"{rejoiner.Desync}\"");
    }

    // ---- shared helpers ----------------------------------------------------

    static void Army(Client c)
    {
        c.Sim.SpawnUnit(1, 8, 8);
        c.Sim.SpawnUnit(1, 11, 8);
        c.Sim.SpawnUnit(1, 8, 11);
        c.Sim.SpawnUnit(2, 44, 40);
        c.Sim.SpawnUnit(2, 47, 40);
    }

    static (Client, Client, RelayTransport) StartMatch()
    {
        var net = new RelayTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Join(a);
        net.Join(b);
        Army(a);
        Army(b);
        return (a, b, net);
    }

    static void Advance(Client a, Client b, RelayTransport net, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            a.SendInput();
            b.SendInput();
            net.Flush();
            a.TryStep();
            b.TryStep();
        }
    }

    static Command Move(int unit, int x, int y) => new Command
    {
        Type = CommandType.Move, UnitIds = new[] { unit }, X = x, Y = y,
    };

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }

    // A transport whose membership can change mid-match, so a client can be
    // dropped and a new one joined the way a real disconnect and reconnect works.
    // Turns are buffered and delivered on Flush, so both clients speak before
    // either listens.
    sealed class RelayTransport : ITransport
    {
        readonly List<Client> _clients = new();
        readonly List<TurnInput> _pending = new();

        public void Join(Client c) => _clients.Add(c);
        public void Drop(Client c) => _clients.Remove(c);

        public int PlayerCount => 2;            // the match is always two players,
                                                // present or not — that's the point
        public void Poll() { }
        public void Send(TurnInput turn) => _pending.Add(turn);

        public void Flush()
        {
            foreach (var c in _clients)
                foreach (var t in _pending)
                    c.Receive(t.Clone());
            _pending.Clear();
        }
    }

    // A transport that sends nowhere: the test plays the part of the network,
    // handing the client exactly the turns it chooses, exactly when it chooses.
    sealed class ManualTransport : ITransport
    {
        Client _local;
        public ManualTransport(int playerCount) => PlayerCount = playerCount;
        public int PlayerCount { get; }
        public void Attach(Client c) => _local = c;
        public void Poll() { }
        public void Send(TurnInput turn) => _local?.Receive(turn.Clone());
    }
}
