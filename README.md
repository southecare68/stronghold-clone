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
│  ├─ Pathfinding/      map, RNG, deterministic grid A*
│  ├─ PathFollowing/    units follow smoothed routes, two-client sync
│  ├─ Combat/           deterministic fighting, RNG sync, win/lose
│  ├─ Economy/          gather/haul/deposit, conservation, two-client sync
│  ├─ Buildings/        placement, footprint blocking, keep drop-off, production
│  ├─ Walls/            curtain walls, gatehouse open/close, sync, rejoin
│  ├─ Siege/            destructible buildings, breaching, sync, rejoin
│  ├─ PointBuy/         data-driven unit designs within a point budget
│  └─ Replay/           record a match and replay it bit-for-bit
└─ prototype-node/      the verified Node proof of the netcode (reference)
   ├─ src/  test/
```

## Run the game
Open the `game/` folder in a **Godot 4.x .NET (C#) editor build** and press
Play. Left-drag to box-select your units; right-click empty ground to move them
(they route around the terrain — a walled gate, a lake, marsh — and the selected
path is drawn) or right-click an enemy to attack. Units fight in melee, health
bars show over the wounded, and the HUD announces the winner when a side is
wiped out. Right-click a resource node to send workers to gather it — they haul
loads back to the drop-off and your stockpile (shown in the HUD) grows. The HUD
also shows the tick, state checksum, and sync state. Press `B`/`K` to place a
barracks/keep at the cursor; right-click your own barracks to train soldiers.
Buildings block movement, so units path around them; lay `W`alls into a curtain
and drop a `G`atehouse in the gap, then right-click your gate to open or close it.
Buildings have HP — right-click an enemy structure with soldiers selected to
besiege it, and a breached wall becomes passable rubble. Press `1`/`2`/`3` to
choose which unit design a barracks trains — units are composed from a point
budget, so a fast Runner and a tanky Brute cost the same but play differently.
Units that pile onto the same spot fan out on screen (a render-only effect; the
simulation is untouched).

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
Netcode proven in Node, ported to C# (bit-identical), and running over real ENet
between two processes: commands cross both ways, checksums match, killing a peer
freezes the match instead of desyncing it, and a fresh process can rejoin a match
in progress. **Cross-architecture determinism is confirmed both headlessly and
live:** the parity test produces the identical checksum (`0xB1A7A676`) on an ARM
Mac and an x86 Linux box, and a real windowed ENet match between the two machines
plays in sync. Units path around terrain with smoothing, fight deterministically (seeded
RNG, in sync across clients and across a mid-fight rejoin), gather resources into
per-player stockpiles, put up buildings whose footprints block movement, train
soldiers from a barracks, raise curtain walls with working gatehouses, and win by
wiping out the other side — all deterministic and cross-architecture-verified.
Phase 2 (the full RTS core) and Phase 3's pillars (walls, gatehouses, siege, and
the custom point-buy unit roster) are complete. See `CONTEXT_HANDOFF.md`.
