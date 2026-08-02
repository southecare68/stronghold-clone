// EnetTransport.cs — ITransport over a real ENet socket. The Godot layer.
//
// This is the ONLY networking-aware file in the project. Everything it does is
// move opaque turn packets between machines; it never touches unit state, never
// decides what a command means, and never advances a tick. The Client refuses to
// step until this class has produced every player's turn, so a slow or dead link
// makes the game stall — which is correct — rather than drift.
//
// Topology: the host is player 1 and ENet peer 1; the joiner is player 2. The
// host relays turns between joiners so three-plus players will work unchanged,
// even though the current match is two. Relaying can deliver a turn twice; that
// is deliberately harmless, because Client.Receive replaces a turn from the same
// (tick, owner) rather than appending it.
//
// Reliable ordered delivery is non-negotiable here. Lockstep has no way to
// reconstruct a lost command — a dropped turn is not a dropped frame, it is a
// tick nobody can ever run. ENet's reliable channel is doing the retransmission
// work that UDP alone would leave to us.

using Godot;
using System.Collections.Generic;
using Sim;
using Netcode;

public sealed class EnetTransport : ITransport
{
    public const int DefaultPort = 27015;

    // Not readonly: host migration replaces the socket wholesale — a joiner whose
    // host vanished tears down its client peer and stands up a server peer in its
    // place, so the departed host can rejoin IT. The player id is likewise no longer
    // fixed by role, so a returning host can reclaim seat 1 against the new host.
    ENetMultiplayerPeer _peer = new();
    bool _isHost;
    readonly int _expectedPlayers;

    Client _local;

    public int PlayerId { get; private set; }
    public string Status { get; private set; } = "starting";
    public bool Failed { get; private set; }

    // Number of remote peers currently connected.
    public int PeersConnected { get; private set; }

    // A client may only run a tick once every player's turn is in hand, so this
    // must NOT report the full roster before everyone has actually connected —
    // otherwise the first player to load would run the whole match alone.
    public int PlayerCount => ReadyToPlay ? _expectedPlayers : int.MaxValue;

    // Three signals, all required, because they become true in different orders
    // on the two ends and any one of them alone lets a client start too early:
    //
    //   * GetConnectionStatus — the joiner's ENet peer list reports the host
    //     while the handshake is still in flight, and PutPacket at that moment
    //     is refused outright ("not currently connected to any server").
    //   * the PeerConnected event — leads on the host, lags on the joiner.
    //   * the raw peer list — leads on the joiner, lags on the host.
    //
    // Waiting for the later of the two peer signals costs a few frames at
    // startup. Starting early costs a turn that no one receives, which is a
    // permanent stall — so this is not a place to be clever.
    public bool AllConnected =>
        !Failed &&
        _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected &&
        _eventPeers >= _expectedPlayers - 1 &&
        PeersConnected >= _expectedPlayers - 1;

    // Connected is not the same as ready to play. A joiner may be arriving in the
    // middle of a match and must adopt the host's state before it does anything;
    // a host must have handed that state over before it ticks again, so the
    // snapshot always reaches the wire ahead of the turns that build on it.
    public bool ReadyToPlay =>
        AllConnected && (_isHost ? _awaitingSnapshot.Count == 0 : _snapshotAdopted);

    int _eventPeers;
    bool _snapshotAdopted;
    readonly List<long> _awaitingSnapshot = new();

    EnetTransport(bool isHost, int playerId, int expectedPlayers)
    {
        _isHost = isHost;
        PlayerId = playerId;
        _expectedPlayers = expectedPlayers;
    }

    public static EnetTransport Host(int port = DefaultPort, int expectedPlayers = 2, int playerId = 1)
    {
        var t = new EnetTransport(isHost: true, playerId, expectedPlayers);
        var err = t._peer.CreateServer(port, expectedPlayers - 1);
        if (err != Error.Ok)
        {
            t.Failed = true;
            t.Status = $"could not listen on port {port}: {err}";
            GD.PrintErr($"[net] {t.Status}");
            return t;
        }
        t.Watch();
        t.Status = $"hosting on port {port}, waiting for {expectedPlayers - 1} player(s)";
        GD.Print($"[net] {t.Status}");
        return t;
    }

    public static EnetTransport Join(string address, int port = DefaultPort, int expectedPlayers = 2, int playerId = 2)
    {
        var t = new EnetTransport(isHost: false, playerId, expectedPlayers);
        var err = t._peer.CreateClient(address, port);
        if (err != Error.Ok)
        {
            t.Failed = true;
            t.Status = $"could not reach {address}:{port}: {err}";
            GD.PrintErr($"[net] {t.Status}");
            return t;
        }
        t.Watch();
        t.Status = $"connecting to {address}:{port}";
        GD.Print($"[net] {t.Status}");
        return t;
    }

    // Host migration. The host we joined has vanished; rather than wait for a host
    // that can never return, WE become the host so it can rejoin us. Tear down the
    // client socket, stand up a server on `port`, and keep everything else — our
    // player id, our Client, its sim frozen at the tick we stalled on. When the
    // returning player connects, the normal join path (SendPendingSnapshots) hands
    // them our current state and the match resumes. Reachability is on the caller:
    // the new host must be reachable at the address it advertises (fine on a LAN).
    public bool PromoteToHost(int port = DefaultPort)
    {
        _peer.Close();
        var server = new ENetMultiplayerPeer();
        var err = server.CreateServer(port, _expectedPlayers - 1);
        if (err != Error.Ok)
        {
            Failed = true;
            Status = $"could not take over hosting on port {port}: {err}";
            GD.PrintErr($"[net] {Status}");
            return false;
        }

        _peer = server;
        _isHost = true;
        Failed = false;
        _eventPeers = 0;
        PeersConnected = 0;
        _snapshotAdopted = false;          // host-side field; unused now that we host
        _awaitingSnapshot.Clear();
        Watch();
        Status = $"took over hosting on port {port} — waiting for the other player to rejoin";
        GD.Print($"[net] {Status}");
        return true;
    }

    // Push a SPECIFIC snapshot to every connected peer, now. Used when the host
    // loads a saved game from the pause menu: the peers adopt it through the very
    // same Kind.Snapshot path a rejoiner uses (Poll → AdoptSnapshot), so no new
    // sync route is introduced. Safe mid-match ONLY because a load is done while
    // paused — the turn stream is empty and the world frozen, so re-seeding every
    // peer to the saved tick cannot diverge.
    public void BroadcastSnapshot(MatchSnapshot snap)
    {
        if (Failed || snap == null) return;
        byte[] bytes = Wire.Serialize(snap);
        _peer.SetTargetPeer(0);                      // 0 = every connected peer
        _peer.PutPacket(bytes);
        GD.Print($"[net] pushed a loaded save to all peers: tick {snap.Tick}, checksum 0x{snap.Checksum:X8}");
    }

    // The Client this transport delivers into. Set once, right after construction.
    public void Attach(Client local) => _local = local;

    public void Send(TurnInput turn)
    {
        // Our own turn never goes near the socket — we already have it, and a
        // client that waited for its own packet to come back would add a round
        // trip of input delay for nothing.
        _local?.Receive(turn.Clone());

        if (Failed) return;

        // Writing to a socket that isn't up yet doesn't queue the turn, it drops
        // it — and a dropped turn is a tick the peer can never run, i.e. a
        // permanent stall rather than a hiccup. Callers must not tick before the
        // match is connected; if one does, say so loudly instead of leaking a
        // hole in the turn stream.
        if (!ReadyToPlay)
        {
            GD.PrintErr($"[net] refusing to send turn {turn.Tick}: match not ready " +
                        "(the caller should not be ticking — this turn would be lost)");
            return;
        }

        var bytes = Wire.Serialize(turn);
        _peer.SetTargetPeer(0);                     // 0 = every connected peer
        _peer.PutPacket(bytes);
    }

    public void Poll()
    {
        if (Failed) return;

        _peer.Poll();
        TrackConnections();
        SendPendingSnapshots();

        while (_peer.GetAvailablePacketCount() > 0)
        {
            int from = _peer.GetPacketPeer();
            byte[] data = _peer.GetPacket();

            var kind = Wire.KindOf(data);
            if (kind == null)
            {
                // Wrong build, corrupt packet, or something that isn't ours.
                // Dropping is safer than guessing; the sender's turn is simply
                // never delivered and this client stalls, which is visible.
                GD.PrintErr($"[net] dropped an unreadable packet ({data?.Length ?? 0} bytes) from peer {from}");
                continue;
            }

            if (kind == Wire.Kind.Snapshot)
            {
                AdoptSnapshot(data, from);
                continue;                       // never relayed: it is addressed to one joiner
            }

            var turn = Wire.Deserialize(data);
            if (turn == null)
            {
                GD.PrintErr($"[net] dropped a malformed turn from peer {from}");
                continue;
            }

            _local?.Receive(turn);

            // Host relays to the other joiners so they hear each other — unless
            // ENet is already doing it, in which case relaying again would just
            // duplicate traffic. (Duplicates would be harmless, since
            // Client.Receive replaces a turn rather than appending it, but
            // harmless is not the same as free.)
            if (_isHost && _expectedPlayers > 2 && !_peer.IsServerRelaySupported())
            {
                _peer.SetTargetPeer(-from);         // negative = all except `from`
                _peer.PutPacket(data);
            }
        }
    }

    // Lockstep cannot paper over a lost command the way a state-sync game papers
    // over a lost snapshot: a missing turn is a tick nobody can ever run. So the
    // channel must be reliable and ordered, and we say so rather than inheriting
    // whatever the default happens to be.
    void Watch()
    {
        _peer.TransferMode = MultiplayerPeer.TransferModeEnum.Reliable;
        _peer.PeerConnected += OnPeerConnected;
        _peer.PeerDisconnected += OnPeerDisconnected;
    }

    // The event count and the peer-list count are tracked SEPARATELY. An earlier
    // version added both into one number and double-counted the same peer,
    // reporting "2/1 connected" — which would let a client believe it had heard
    // from a player who does not exist.
    void OnPeerConnected(long id)
    {
        _eventPeers++;
        GD.Print($"[net] peer {id} connected");

        // Whoever joins gets the match as it currently stands. Queued rather than
        // sent here: this fires from inside Poll, before the peer list has caught
        // up, and writing to a socket that is not fully up drops the packet.
        if (_isHost) _awaitingSnapshot.Add(id);
    }

    // Sent once the link is genuinely established, and BEFORE any turn, because
    // ReadyToPlay keeps the host from ticking until this queue is empty. On a
    // reliable ordered channel that ordering then holds all the way to the joiner.
    void SendPendingSnapshots()
    {
        if (!_isHost || _awaitingSnapshot.Count == 0 || _local == null) return;
        if (!AllConnected) return;

        var snap = _local.CaptureSnapshot();
        byte[] bytes = Wire.Serialize(snap);

        foreach (long id in _awaitingSnapshot)
        {
            _peer.SetTargetPeer((int)id);
            _peer.PutPacket(bytes);
            GD.Print($"[net] sent match state to peer {id}: tick {snap.Tick}, " +
                     $"{snap.Units.Length} units, {snap.PendingTurns.Length} turns already in flight, " +
                     $"checksum 0x{snap.Checksum:X8}");
        }
        _awaitingSnapshot.Clear();
        _peer.SetTargetPeer(0);
    }

    void AdoptSnapshot(byte[] data, int from)
    {
        var snap = Wire.DeserializeSnapshot(data);
        if (snap == null)
        {
            GD.PrintErr($"[net] match state from peer {from} was unreadable — cannot join");
            return;
        }

        bool ok = _local != null && _local.AdoptSnapshot(snap);
        _snapshotAdopted = true;                // ok or not, we are done waiting
        Status = ok ? "connected" : "joined with a state mismatch";

        if (ok)
            GD.Print($"[net] joined the match at tick {snap.Tick}, " +
                     $"checksum 0x{snap.Checksum:X8} verified against our own");
        else
            GD.PrintErr("[net] the state we rebuilt does not match what the host sent — " +
                        "joining anyway so the mismatch is visible rather than silent");
    }

    void OnPeerDisconnected(long id)
    {
        _eventPeers--;
        GD.PrintErr($"[net] peer {id} disconnected — the match cannot continue without their input");
    }

    void TrackConnections()
    {
        int actual = _peer.Host?.GetPeers().Count ?? 0;
        if (actual == PeersConnected) return;

        PeersConnected = actual;
        Status = AllConnected ? "connected" : "waiting for more players";
        GD.Print($"[net] peers: {PeersConnected}/{_expectedPlayers - 1} ({Status})");
    }

    public void Close()
    {
        _peer.Close();
        Status = "closed";
    }
}
