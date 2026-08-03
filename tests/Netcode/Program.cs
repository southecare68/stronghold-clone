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
        SkirmishSnapshotRoundTrip();
        ConsentPauseFreezesInLockstep();
        PauseStateSurvivesTheWire();
        AiTakesOverWhenAPlayerLeaves();
        TakeoverGrantsNoHandicap();
        AiOwnershipSurvivesTheWire();
        HostMigrationSeatSwap();
        SaveLoadReseedsInLockstep();
        RestartReseedsToFreshOpening();
        RoamingScoutStaysDeterministic();
        ScoutReportsWhatItFinds();
        GuardingStaysDeterministic();

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
        // The snapshot carries a StateChecksum (everything that can diverge), so
        // that is what a sim rebuilt from it must reproduce.
        Check($"a sim rebuilt from the bytes hashes identically (0x{snap.Checksum:X8})",
              fresh.Sim.StateChecksum() == snap.Checksum);
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

    // Regression for the two-window DESYNC@0: the real Skirmish start runs with fog
    // ON but reveals nothing until the first tick, so its Explored dict is empty. A
    // rejoiner adopts that snapshot and recomputes visibility, which must NOT leave
    // an empty Explored owner entry behind — StateChecksum hashes the entry count,
    // so it would flag a phantom desync at tick 0. Also covers a joiner that built
    // the world itself first, the way World3D does.
    static void SkirmishSnapshotRoundTrip()
    {
        Console.WriteLine("\nskirmish snapshot round-trip (fog on):");
        var host = new Simulation(TileMap.Skirmish(128));
        Skirmish.Setup(host, 128);
        uint hostSum = host.Snapshot().Checksum;
        var back = Wire.DeserializeSnapshot(Wire.Serialize(host.Snapshot()));

        var fresh = new Simulation(TileMap.Skirmish(128));
        fresh.Restore(back);
        Check($"fresh joiner reproduces host (0x{fresh.StateChecksum():X8} == 0x{hostSum:X8})",
              fresh.StateChecksum() == hostSum);

        var join = new Simulation(TileMap.Skirmish(128));
        Skirmish.Setup(join, 128);           // built the world itself, then adopts
        join.Restore(back);
        Check($"setup-then-adopt joiner reproduces host (0x{join.StateChecksum():X8} == 0x{hostSum:X8})",
              join.StateChecksum() == hostSum);

        // The match-length dial rides the snapshot, so a rejoiner paces its game the
        // same as the host (a mismatch would silently desync every hold and cost).
        var epic = new Simulation(TileMap.Open(48)) { PaceScale = 6 };
        var rejoin = new Simulation(TileMap.Open(48));
        rejoin.Restore(Wire.DeserializeSnapshot(Wire.Serialize(epic.Snapshot())));
        Check($"the PaceScale dial survives the wire (got {rejoin.PaceScale})", rejoin.PaceScale == 6);
    }

    static Command Vote(bool pause) => new Command
    {
        Type = CommandType.SetPauseVote, X = pause ? 1 : 0,
    };

    static (int, int) UnitPos(Simulation sim, int id)
    {
        foreach (var u in sim.Units) if (u.Id == id) return (u.X, u.Y);
        return (int.MinValue, int.MinValue);
    }

    // The heart of the multiplayer pause: it must ride the deterministic command
    // stream, so two clients freeze and thaw on the SAME tick and never desync —
    // and while frozen, game-time (calendar, cooldowns, victory clock) must hold
    // still even though the lockstep tick keeps advancing underneath.
    static void ConsentPauseFreezesInLockstep()
    {
        Console.WriteLine("\nconsent-pause (multiplayer):");
        var (a, b, net) = StartMatch();
        a.Sim.PauseRoster = 2;
        b.Sim.PauseRoster = 2;

        // Send a unit on a long march and let game-time get well underway.
        var spawnPos = UnitPos(a.Sim, 1);
        a.Issue(Move(unit: 1, x: 120, y: 120));
        Advance(a, b, net, 60);
        Check("the unit is mid-march before we pause", UnitPos(a.Sim, 1) != spawnPos);

        // One player alone voting to pause does NOT stop the match — pause is unanimous.
        a.Issue(Vote(true));
        Advance(a, b, net, 10);
        Check("one vote does not pause (unanimity required)", !a.Sim.GamePaused && !b.Sim.GamePaused);

        // The second player agrees → both freeze, on the same tick, in the same state.
        b.Issue(Vote(true));
        Advance(a, b, net, 10);
        Check("both agree → both paused", a.Sim.GamePaused && b.Sim.GamePaused);
        Check("paused in lockstep (same tick, same checksum)",
              a.Sim.TickNumber == b.Sim.TickNumber && a.Sim.StateChecksum() == b.Sim.StateChecksum());

        int clockAtPause = a.Sim.GameClock;
        int monthAtPause = a.Sim.GameMonth;
        int tickAtPause = a.Sim.TickNumber;
        var posAtPause = UnitPos(a.Sim, 1);

        // Run a long paused span. The lockstep keeps ticking (turns must keep flowing)
        // but nothing in the world may move.
        Advance(a, b, net, 400);

        Check($"the lockstep keeps ticking while paused ({a.Sim.TickNumber} == {tickAtPause}+400)",
              a.Sim.TickNumber == tickAtPause + 400);
        Check($"game-time is frozen (clock held at {clockAtPause})", a.Sim.GameClock == clockAtPause);
        Check($"the calendar holds (still Year/Month {monthAtPause})", a.Sim.GameMonth == monthAtPause);
        Check("units do not move while paused", UnitPos(a.Sim, 1) == posAtPause);
        Check("both worlds still agree after 400 paused ticks",
              a.Sim.StateChecksum() == b.Sim.StateChecksum());

        // Resume is unanimous too: one player clearing their vote is not enough.
        a.Issue(Vote(false));
        Advance(a, b, net, 10);
        Check("one resume vote does not resume (unanimity required)", a.Sim.GamePaused && b.Sim.GamePaused);

        // Both clear → the match runs again, and game-time picks up EXACTLY where it
        // froze — it must not have skipped the 400 paused ticks.
        b.Issue(Vote(false));
        Advance(a, b, net, 40);
        Check("both agree → resumed", !a.Sim.GamePaused && !b.Sim.GamePaused);
        Check($"game-time resumes where it froze, not skipped ahead (clock {a.Sim.GameClock}, was {clockAtPause})",
              a.Sim.GameClock > clockAtPause && a.Sim.GameClock < clockAtPause + 100);
        Check("units march again after resume", UnitPos(a.Sim, 1) != posAtPause);
        Check("still in perfect lockstep after the whole pause cycle",
              a.Sim.StateChecksum() == b.Sim.StateChecksum());
    }

    // A rejoiner must adopt the pause exactly, or a match paused when someone drops
    // would thaw on their machine alone the moment they reconnect.
    static void PauseStateSurvivesTheWire()
    {
        Console.WriteLine("\npause state on the wire:");
        var paused = new Simulation(TileMap.Open(48)) { PauseRoster = 2, GamePaused = true, PausedTicks = 137 };
        var back = new Simulation(TileMap.Open(48));
        back.Restore(Wire.DeserializeSnapshot(Wire.Serialize(paused.Snapshot())));
        Check($"GamePaused survives the wire (got {back.GamePaused})", back.GamePaused);
        Check($"PausedTicks survives the wire (got {back.PausedTicks})", back.PausedTicks == 137);
        Check($"PauseRoster survives the wire (got {back.PauseRoster})", back.PauseRoster == 2);
        Check("a rejoiner reproduces the paused world exactly",
              back.StateChecksum() == paused.Snapshot().Checksum);
    }

    static Command Leave(AiLevel level, VictoryPath path) => new Command
    {
        Type = CommandType.LeaveToAi, X = (int)level, Y = (int)path,
    };

    // Save/load from the pause menu. A save is one MatchSnapshot in the wire format;
    // loading has EVERY client adopt the same saved bytes (solo does it locally, a
    // networked host re-distributes them through the snapshot handshake). Both must
    // land on the saved tick byte-identically and resume in perfect lockstep — the
    // same guarantee a rejoin gives, which is why loading rides that machinery.
    static void SaveLoadReseedsInLockstep()
    {
        Console.WriteLine("\nsave / load re-seed:");
        var (a, b, net) = StartMatch();
        a.Issue(Move(unit: 1, x: 30, y: 30));
        b.Issue(Move(unit: 4, x: 20, y: 20));
        Advance(a, b, net, 50);

        // "Save" — the exact bytes a .khsave file would hold.
        byte[] saveBytes = Wire.Serialize(a.Sim.Snapshot());
        int savedTick = a.Sim.TickNumber;

        // Play well past the save so the live match has clearly moved on.
        Advance(a, b, net, 120);
        Check($"the match moved past the save ({a.Sim.TickNumber} vs saved {savedTick})",
              a.Sim.TickNumber == savedTick + 120);

        // "Load" — every client adopts the saved bytes (each deserializes its own copy,
        // as a host and a joiner would off the wire).
        var loaded = Wire.DeserializeSnapshot(saveBytes);
        Check("the save round-trips through the wire format", loaded != null);
        bool aOk = a.AdoptSnapshot(loaded);
        bool bOk = b.AdoptSnapshot(Wire.DeserializeSnapshot(saveBytes));
        Check("both clients adopt the save", aOk && bOk);
        Check($"both rewound to the saved tick ({a.Sim.TickNumber})",
              a.Sim.TickNumber == savedTick && b.Sim.TickNumber == savedTick);
        Check("both reproduce the saved world exactly",
              a.Sim.StateChecksum() == loaded.Checksum && b.Sim.StateChecksum() == loaded.Checksum);

        // And the match carries on from the loaded point, in perfect lockstep.
        int desyncs = 0;
        for (int i = 0; i < 200; i++)
        {
            a.SendInput(); b.SendInput(); net.Flush(); a.TryStep(); b.TryStep();
            if (a.Sim.Checksum() != b.Sim.Checksum()) desyncs++;
        }
        Check("play resumes from the load, in sync every tick", desyncs == 0);
        Check("no desync reported by either side", a.Desync == null && b.Desync == null);
    }

    // Host migration's deterministic core. When the HOST drops, the surviving JOINER
    // (player 2) becomes the source of truth and the returning host (player 1) rejoins
    // by adopting the joiner's snapshot — reclaiming its own seat. The socket role-swap
    // (client peer → server peer) lives in EnetTransport and needs real hardware to
    // exercise; what's proven here is that the rejoin machinery is seat-agnostic and
    // the match resumes in perfect lockstep with the roles reversed.
    static void HostMigrationSeatSwap()
    {
        Console.WriteLine("\nhost migration (the joiner becomes host):");
        var (host, peer, net) = StartMatch();   // host = player 1, peer = player 2

        host.Issue(Move(unit: 1, x: 35, y: 30));
        peer.Issue(Move(unit: 4, x: 20, y: 20));
        Advance(host, peer, net, 60);

        // The HOST (player 1) vanishes. The joiner runs out its committed input and
        // freezes — it is now the survivor and the sole source of truth.
        net.Drop(host);
        int droppedAt = peer.Sim.TickNumber;
        for (int i = 0; i < 50; i++) { peer.SendInput(); net.Flush(); peer.TryStep(); }
        Check($"the surviving joiner freezes after the host drops (tick {peer.Sim.TickNumber})",
              peer.Stalled && peer.Sim.TickNumber == droppedAt + Client.InputDelay);

        // Player 2 takes over hosting; player 1 relaunches and rejoins, reclaiming seat 1.
        var returning = new Client(1, net);     // fresh process, but the SAME seat
        Army(returning);
        var snap = peer.CaptureSnapshot();       // the NEW host (player 2) hands out the state
        bool adopted = returning.AdoptSnapshot(snap);
        net.Join(returning);

        Check("the returning host adopts the new host's snapshot", adopted);
        Check($"it lands on the survivor's tick ({returning.Sim.TickNumber})",
              returning.Sim.TickNumber == peer.Sim.TickNumber);
        Check("and reclaims seat 1", returning.PlayerId == 1);
        Check("the two worlds hash identically", returning.Sim.Checksum() == peer.Sim.Checksum());

        // The match carries on with roles reversed (2 hosting, 1 rejoined), in lockstep.
        int desyncs = 0;
        for (int i = 0; i < 200; i++)
        {
            peer.SendInput();
            returning.SendInput();
            net.Flush();
            peer.TryStep();
            returning.TryStep();
            if (peer.Sim.Checksum() != returning.Sim.Checksum()) desyncs++;
        }
        Check($"the survivor unfroze and ran on (tick {peer.Sim.TickNumber})",
              peer.Sim.TickNumber > droppedAt + Client.InputDelay);
        Check("200 ticks after migration, still in sync every tick", desyncs == 0);
        Check("no desync reported by either side", peer.Desync == null && returning.Desync == null);

        // The returned host commands its OWN realm (owner 1) again.
        returning.Issue(Move(unit: 1, x: 8, y: 8));
        for (int i = 0; i < 60; i++) { peer.SendInput(); returning.SendInput(); net.Flush(); peer.TryStep(); returning.TryStep(); }
        var u1 = peer.Sim.Units.Find(u => u.Id == 1);
        Check("the returned host commands its own realm again",
              u1.Tx == Fixed.FromInt(8) && u1.Ty == Fixed.FromInt(8));
        Check("still in sync afterwards", peer.Sim.Checksum() == returning.Sim.Checksum());
    }

    // When a player leaves, the AI must take their realm WITHOUT stalling the
    // survivor — the departed seat sends no more turns, so the lockstep would jam
    // forever unless those (empty) turns are synthesized while the in-sim AI plays on.
    static void AiTakesOverWhenAPlayerLeaves()
    {
        Console.WriteLine("\nAI takeover on leave:");
        var (a, b, net) = StartMatch();

        // Let both realms get going.
        a.Issue(Move(unit: 1, x: 30, y: 30));
        b.Issue(Move(unit: 4, x: 20, y: 20));
        Advance(a, b, net, 40);

        int unitsAtLeave = a.Sim.Units.Count;

        // Player 2 leaves, handing their realm to a Normal AI. Both are still live for
        // a few ticks, so the command executes in lockstep and both sims must agree.
        b.Issue(Leave(AiLevel.Normal, VictoryPath.Domain));
        Advance(a, b, net, 8);
        Check("both sims flag the departed player as AI", a.Sim.IsAi(2) && b.Sim.IsAi(2));
        Check("takeover is deterministic (both worlds still agree at the handoff)",
              a.Sim.StateChecksum() == b.Sim.StateChecksum());
        Check($"takeover grants no bonus army — the AI inherits the realm as-is ({a.Sim.Units.Count} vs {unitsAtLeave})",
              a.Sim.Units.Count == unitsAtLeave);

        // Now player 2 is really gone: it sends nothing further. The survivor must
        // keep ticking, driving the vacated realm as an AI, never stalling.
        net.Drop(b);
        int tickAtLeave = a.Sim.TickNumber;
        bool everStalled = false;
        for (int i = 0; i < 200; i++)
        {
            a.SendInput();
            net.Flush();
            a.TryStep();
            if (a.Stalled) everStalled = true;
        }
        Check($"the survivor plays on after the peer is gone (tick {a.Sim.TickNumber})",
              a.Sim.TickNumber >= tickAtLeave + 190);
        Check("it never stalls waiting for the player who left", !everStalled);
        Check("no desync on the survivor", a.Desync == null);
        Check("the AI-run realm is still alive", a.Sim.Units.Count > 0);
    }

    // The mid-match takeover must not hand the AI the fresh-bot setup handicap —
    // it inherits an established realm, not a new one.
    static void TakeoverGrantsNoHandicap()
    {
        Console.WriteLine("\ntakeover grants no handicap:");
        var fresh = new Simulation(TileMap.Open(48));
        fresh.SpawnUnit(2, 20, 20);
        fresh.SpawnUnit(2, 22, 20);
        int before = fresh.Units.Count;
        fresh.TakeOverWithAi(2, AiLevel.Hard, VictoryPath.Domain);
        Check($"TakeOverWithAi adds no units ({fresh.Units.Count} == {before})", fresh.Units.Count == before);
        Check("but it does mark the owner as AI", fresh.IsAi(2));

        var bonus = new Simulation(TileMap.Open(48));
        bonus.SpawnUnit(2, 20, 20);
        bonus.EnableAi(2, AiLevel.Hard);
        Check("whereas EnableAi (a fresh bot) still grants its handicap peasants",
              bonus.Units.Count > 1);
    }

    // A rejoiner must learn who the computer is now running, or a match where a
    // player left would have the bot on one machine and a frozen realm on the other.
    static void AiOwnershipSurvivesTheWire()
    {
        Console.WriteLine("\nAI ownership on the wire:");
        var host = new Simulation(TileMap.Open(48));
        host.SpawnUnit(2, 20, 20);
        host.TakeOverWithAi(2, AiLevel.Hard, VictoryPath.Science);
        var back = new Simulation(TileMap.Open(48));
        back.Restore(Wire.DeserializeSnapshot(Wire.Serialize(host.Snapshot())));
        Check($"the AI seat survives the wire (IsAi(2) = {back.IsAi(2)})", back.IsAi(2));
        Check("a rejoiner reproduces the world exactly, bot and all",
              back.StateChecksum() == host.Snapshot().Checksum);
    }

    // Restart is a re-seed like Load, but the snapshot is a freshly-built opening
    // rather than a file: every client jumps back to an identical tick 0 and the
    // rematch runs in lockstep. (Clients keep their map across an adopt, so both must
    // hold the same Skirmish map — as they do in a real match — hence the setup here.)
    static void RestartReseedsToFreshOpening()
    {
        Console.WriteLine("\nrestart match re-seed:");
        var net = new RelayTransport();
        var a = new Client(1, net, TileMap.Skirmish(128));
        var b = new Client(2, net, TileMap.Skirmish(128));
        Skirmish.Setup(a.Sim, 128);
        Skirmish.Setup(b.Sim, 128);
        net.Join(a);
        net.Join(b);

        Advance(a, b, net, 80);
        Check($"the match is underway (tick {a.Sim.TickNumber})", a.Sim.TickNumber == 80);

        // "Restart" — build a fresh opening world and have every client adopt it.
        var fresh = new Simulation(TileMap.Skirmish(128));
        Skirmish.Setup(fresh, 128);
        var snap = fresh.Snapshot();
        Check("the fresh opening differs from the played-on match", a.Sim.StateChecksum() != snap.Checksum);

        bool aOk = a.AdoptSnapshot(Wire.DeserializeSnapshot(Wire.Serialize(snap)));
        bool bOk = b.AdoptSnapshot(Wire.DeserializeSnapshot(Wire.Serialize(snap)));
        Check("both clients adopt the fresh opening", aOk && bOk);
        Check($"both restart at tick 0 (a={a.Sim.TickNumber}, b={b.Sim.TickNumber})",
              a.Sim.TickNumber == 0 && b.Sim.TickNumber == 0);
        Check("both hold the identical fresh world",
              a.Sim.StateChecksum() == snap.Checksum && b.Sim.StateChecksum() == snap.Checksum);

        int desyncs = 0;
        for (int i = 0; i < 200; i++)
        {
            a.SendInput(); b.SendInput(); net.Flush(); a.TryStep(); b.TryStep();
            if (a.Sim.Checksum() != b.Sim.Checksum()) desyncs++;
        }
        Check("the rematch runs from the top, in sync every tick", desyncs == 0);
        Check("no desync reported by either side", a.Desync == null && b.Desync == null);
    }

    // A roaming scout picks its own targets (rings outward for dark ground, a seeded
    // wander as fallback) and uses the RNG — all of which must be byte-identical on
    // every machine, or a free-roaming unit would silently desync the match.
    static void RoamingScoutStaysDeterministic()
    {
        Console.WriteLine("\nroaming scout determinism:");
        var net = new RelayTransport();
        var a = new Client(1, net, TileMap.Skirmish(128));
        var b = new Client(2, net, TileMap.Skirmish(128));
        Skirmish.Setup(a.Sim, 128);
        Skirmish.Setup(b.Sim, 128);
        net.Join(a);
        net.Join(b);

        // Same scout on both sims (identical worlds → identical id), then set it roaming.
        var s = a.Sim.SpawnUnit(1, 40, 40, 4);
        b.Sim.SpawnUnit(1, 40, 40, 4);
        var start = (s.X, s.Y);
        a.Issue(new Command { Type = CommandType.SetRoam, UnitIds = new[] { s.Id }, X = 1 });

        int desyncs = 0;
        for (int i = 0; i < 400; i++)
        {
            a.SendInput(); b.SendInput(); net.Flush(); a.TryStep(); b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) desyncs++;
        }
        Check("a free-roaming scout stays in perfect lockstep", desyncs == 0);
        var now = a.Sim.Units.Find(u => u.Id == s.Id);
        Check("and it actually roamed off on its own", now != null && (now.X, now.Y) != start);
    }

    // The report half: a roaming scout calls out the enemy — a keep as a stronghold —
    // and a non-scout ignores the order entirely.
    static void ScoutReportsWhatItFinds()
    {
        Console.WriteLine("\nscout roam & report:");
        var sim = new Simulation(TileMap.Skirmish(128));
        Skirmish.Setup(sim, 128);
        sim.FogEnabled = true;

        int kx = -1, ky = -1;
        foreach (var b in sim.Buildings)
            if (b.Type == BuildingType.Keep && b.Owner == 2) { kx = b.CenterX; ky = b.CenterY; break; }
        Check("found the enemy keep to spot", kx >= 0);

        var scout = sim.SpawnUnit(1, kx - 6, ky, 4);
        var soldier = sim.SpawnUnit(1, kx - 6, ky + 2, 0);
        scout.Roaming = true;

        sim.Tick(new[] { new Command { Owner = 1, Seq = 1, Type = CommandType.SetRoam, UnitIds = new[] { soldier.Id }, X = 1 } });
        Check("a non-scout ignores the roam order", !soldier.Roaming);
        Check("the scout is roaming", scout.Roaming);

        bool reported = false;
        for (int i = 0; i < 12 && !reported; i++)
        {
            sim.Tick(Array.Empty<Command>());
            foreach (var s in sim.ScoutSightings)
                if (s.Owner == 1 && s.Enemy == 2 && s.Kind == SightingKind.Stronghold) reported = true;
        }
        Check("the roaming scout reported the enemy stronghold", reported);
    }

    // A guard auto-intercepts intruders — targets picked in-sim from HomeRect + fog —
    // so like the roaming scout it must be byte-identical on every machine.
    static void GuardingStaysDeterministic()
    {
        Console.WriteLine("\nguard determinism:");
        var net = new RelayTransport();
        var a = new Client(1, net, TileMap.Skirmish(128));
        var b = new Client(2, net, TileMap.Skirmish(128));
        Skirmish.Setup(a.Sim, 128);
        Skirmish.Setup(b.Sim, 128);
        net.Join(a);
        net.Join(b);

        int kx = 0, ky = 0;
        foreach (var bl in a.Sim.Buildings)
            if (bl.Type == BuildingType.Keep && bl.Owner == 1) { kx = bl.CenterX; ky = bl.CenterY; }

        // A guard and an intruder near player 1's keep, spawned identically on both sims.
        var g = a.Sim.SpawnUnit(1, kx + 3, ky, 0); b.Sim.SpawnUnit(1, kx + 3, ky, 0);
        var foe = a.Sim.SpawnUnit(2, kx + 6, ky, 0); b.Sim.SpawnUnit(2, kx + 6, ky, 0);
        a.Issue(new Command { Type = CommandType.SetGuard, UnitIds = new[] { g.Id }, X = 1 });

        int desyncs = 0;
        for (int i = 0; i < 300; i++)
        {
            a.SendInput(); b.SendInput(); net.Flush(); a.TryStep(); b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) desyncs++;
        }
        Check("a guard auto-intercepting stays in perfect lockstep", desyncs == 0);
        var f = a.Sim.Units.Find(u => u.Id == foe.Id);
        Check("and the guard actually engaged the intruder", f == null || f.Hp < f.MaxHp);
    }

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
