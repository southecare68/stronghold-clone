# Stronghold Clone

A multiplayer castle RTS built from scratch in **Godot 4 + C#**, using
deterministic lockstep networking. New game, own art — not a mod.

> New here? Read **`CONTEXT_HANDOFF.md`** first, then **`ARCHITECTURE.md`**.

## Repo layout
```
stronghold-clone/
├─ ARCHITECTURE.md      engine choice, architecture, full roadmap
├─ CONTEXT_HANDOFF.md   briefing to resume work (start here)
├─ game/                the Godot 4 C# project
│  ├─ project.godot
│  ├─ Main.tscn
│  ├─ StrongholdClone.csproj
│  ├─ Sim/              engine-agnostic deterministic simulation (C#)
│  │  ├─ Fixed.cs       fixed-point math (no floats in the sim)
│  │  ├─ Rng.cs         seeded integer RNG (System.Random is banned here)
│  │  ├─ TileMap.cs     terrain grid, integer movement costs
│  │  ├─ PathFinder.cs  deterministic grid A* (total tie-break order)
│  │  ├─ Simulation.cs  game state + Tick() + checksum
│  │  └─ Lockstep.cs    client, turns, input delay, ITransport seam
│  ├─ Net/              engine-agnostic protocol (Godot-free, so it's testable)
│  │  ├─ Wire.cs        turn serialization, explicit little-endian
│  │  └─ MatchCode.cs   endpoint <-> XXXXX-XXXXX join code
│  └─ Scripts/          the Godot layer
│     ├─ Main.cs        renders the sim, mouse -> commands
│     └─ EnetTransport.cs   ITransport over a real ENet socket
├─ tests/               console tests; no Godot, so they run anywhere dotnet does
│  ├─ SimParity/        C# sim reproduces the Node reference exactly
│  ├─ InputSlice/       the mouse flow, headless
│  ├─ CommandOrder/     command ordering is total (no arrival-order dependence)
│  ├─ Netcode/          wire format, join codes, stalling, desync detection
│  └─ Pathfinding/      map, RNG, deterministic grid A*
└─ prototype-node/      the verified Node proof of the netcode (reference)
   ├─ src/  test/
```

## Run the game
Open the `game/` folder in a **Godot 4.x .NET (C#) editor build** and press
Play. Left-drag to box-select your units, right-click to move them. The HUD
shows the tick, state checksum, and sync state.

The simulation runs at 20 Hz but draws smoothly: units are rendered between
their last two tick positions, so motion doesn't step with the tick rate. That
is a rendering concern only — nothing interpolated ever reaches the sim. Run
with `--debug-interp` to see the drawn position printed beside the true one.

Without arguments the game runs both players in one window, which is the only
mode that can prove sync on a single machine: two independent simulations, same
input, compared every tick.

## Play across two machines
`dotnet` must be on PATH or Godot cannot load .NET and crashes at startup.
```
# machine 1
Godot --path game -- --host
# machine 2  (the host's waiting screen prints its address and match code)
Godot --path game -- --join=192.168.0.209
Godot --path game -- --code=60N00-D2TC7      # same thing, shorter to read out
```
Only commands cross the network, never unit state. A client refuses to advance
a tick until it holds every player's input for it, so a broken link stalls the
match rather than letting the two worlds drift apart. Every turn carries a state
checksum, so a real desync is caught within a few ticks and named.

If a player drops, the match freezes rather than continuing without them, and
they can reconnect: the host hands over a snapshot of the world and the returning
client verifies it hashes to what the host said before playing on.

A match code is the host's IP and port in base32 — a friendlier spelling of an
address, not NAT traversal. Same-LAN or forwarded ports only.

## Run the tests (no engine needed)
```
dotnet run --project tests/SimParity     # and InputSlice, CommandOrder, Netcode
cd prototype-node
node test/sync.test.js          # two clients identical for 300 ticks
node test/float-hazard.test.js  # why the sim forbids floating point
```

## Status
Netcode proven in Node, ported to C# (bit-identical), and now running over real
ENet between two processes: commands cross both ways, checksums match, killing a
peer freezes the match instead of desyncing it, and a fresh process can rejoin a
match in progress. **Still unproven: the cross-architecture run** (ARM Mac vs
x86 Linux), which is the one that tests the fixed-point rule. See
`CONTEXT_HANDOFF.md`.
