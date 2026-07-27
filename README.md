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
│  │  ├─ Vision.cs      fog of war: per-player sight and explored memory
│  │  ├─ Skirmish.cs    the 1v1 starting position (so tests can check it)
│  │  ├─ Simulation.cs  game state + Tick() + checksum
│  │  └─ Lockstep.cs    client, turns, input delay, ITransport seam
│  ├─ Net/              engine-agnostic protocol (Godot-free, so it's testable)
│  │  ├─ Wire.cs        turn serialization, explicit little-endian
│  │  └─ MatchCode.cs   endpoint <-> XXXXX-XXXXX join code
│  ├─ Audio/            engine-agnostic sound synthesis (no audio files at all)
│  │  ├─ Synth.cs       every effect generated from noise and envelopes
│  │  └─ Music.cs       the score: Compose() writes notes, Render() plays them
│  ├─ Art/              baked 2D sprites (small, committed — see tools/bake)
│  │  ├─ terrain/       ground/rock/marsh tiles
│  │  ├─ buildings/     keep, barracks, wall, gatehouse
│  │  └─ units/         soldier/runner/brute/archer: 8 facings x idle + walk
│  └─ Scripts/          the Godot layer
│     ├─ Main.cs        renders the sim, mouse -> commands
│     ├─ SpriteBank.cs  loads the baked sprites, falls back to shapes
│     ├─ Sound.cs       voices, positional playback, mix levels
│     ├─ MusicPlayer.cs seamless loops, cross-fading between moods
│     └─ EnetTransport.cs   ITransport over a real ENet socket
├─ tools/               offline tooling
│  └─ bake/             render the 3D asset packs into 2D sprites (run.sh)
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
│  ├─ Replay/           record a match and replay it bit-for-bit
│  ├─ Fog/              fog of war: sight, memory, and the orders it gates
│  ├─ Woodcutting/      the self-running wood chain (hut -> cutter -> storage)
│  └─ Audio/            synth and score, checked numerically (no speakers needed)
└─ prototype-node/      the verified Node proof of the netcode (reference)
   ├─ src/  test/
```

## Run the game
**Fresh Mac?** From the cloned repo, `./setup-macos.sh` checks the toolchain,
builds, generates this machine's Godot import cache, and prints how to play
(`./setup-macos.sh --play` launches straight away; append game flags like
`--ai=hard` or `--no-fog`). Install the Godot 4.7 .NET (mono) build and the
.NET 8 SDK, and copy in the git-ignored `game/Assets/` art pack, first — the
script says exactly how if either is missing.

Open the `game/` folder in a **Godot 4.x .NET (C#) editor build** and press
Play. Left-drag to box-select your units; right-click empty ground to move them
(they route around the terrain — a walled gate, a lake, marsh — and the selected
path is drawn) or right-click an enemy to attack. Units fight in melee, health
bars show over the wounded, and the HUD announces the winner when a side is
wiped out. Right-click a resource node to send workers to gather it — they haul
loads back to the drop-off and your stockpile (shown in the HUD) grows. The HUD
also shows the tick, state checksum, and sync state. Press `B`/`K` to place a
barracks/keep at the cursor; right-click your own barracks to train soldiers.
Press `H` to raise a **woodcutter's hut** in a forest and `Q` for a **quarry** on a
stone deposit — each runs itself, breeding a peasant who harvests the nearest nodes
and hauls the goods back with no orders from you — and `J` for a **storehouse**, a
closer drop-off so the round trip is shorter.
Buildings block movement, so units path around them; lay `W`alls into a curtain
and drop a `G`atehouse in the gap, then right-click your gate to open or close it.
Buildings have HP — right-click an enemy structure with soldiers selected to
besiege it, and a breached wall becomes passable rubble. Press `1`/`2`/`3` to
choose which unit design a barracks trains — units are composed from a point
budget, so a fast Runner, a tanky Brute, and a long-range Archer cost the same but
play differently. Ranged units loose arrows at their targets from a distance.
Units that pile onto the same spot fan out on screen (a render-only effect; the
simulation is untouched).

Mouse wheel zooms (toward the cursor), and middle-drag or the arrow keys pan the
camera around the map. A minimap in the bottom-right shows the whole battlefield
with your current view outlined — click it to jump the camera there.

Matches are fought on a 128×128 skirmish map, far larger than the window. The two
keeps face each other across a rock ridge that runs the length of the map, broken
by three passes: the middle one is the short road but its marsh aprons slow
anyone taking it, and the outer two are clean going but a long way round. Two
lakes and a pair of outcrops break up the rest. Each side has safe wood and stone
behind its base, and there is a richer patch out by each far pass worth leaving
home for. The camera starts on your own keep.

**Fog of war** is on, and it is a rule rather than a screen effect. You start
seeing only your own base; ground you have never visited is black, ground you
scouted but have since left is dimmed and shows the terrain, buildings and
resource patches you remember but not what is moving through it now. Rock blocks
line of sight, so the ridge genuinely hides an army massing behind it — but you
can see clean across water. Because it is enforced in the simulation, you cannot
order an attack on a unit you cannot see, send a worker to a patch you have never
found, or build on ground you have never visited; units will not auto-acquire a
target through fog either, though one they are already fighting is still chased.
Press `F` to reveal the whole map — a display switch only, which is worth trying
precisely because the orders stay refused.

**Sound.** There are no audio files in this repo: every effect is generated from
arithmetic at startup (`game/Audio/Synth.cs`), so the "assets" are source code you
can read and retune a number at a time. Orders and selections answer back
immediately, blows and bowshots come from where they land, a refused order says
so, and everything is positional — a fight across the map sounds like it is
across the map, and one you cannot see makes no sound at all, because audible
fog would hand back exactly what the fog was there to withhold. `M` mutes, `-`
and `=` set the volume.

**Art.** Units and buildings are 2D sprites baked from 3D model packs — the same
trick the classic isometric RTS used: render each model once from a fixed 3/4
view into a PNG, then draw flat sprites and never touch a mesh at runtime. Units
carry eight facings and animate — the models ship no animation clips, so the walk
cycle, attack swing, death topple and standing pose are authored by posing the
rigged skeleton at bake time (which also lifts the models out of their T-pose).
Units idle when still, walk when moving, swing while fighting, and topple and fade
where they fall; terrain is textured grass, rock and marsh. The sprites live
in `game/Art/` (small, committed); the multi-gigabyte source packs do not (see
`tools/bake/` for how they are produced). If the sprites are absent the game
draws its original coloured shapes instead, so it runs the same with or without
them — the art is an overlay on a game that was already complete, not a
dependency.

**Music** is generated too, and adapts. Three tracks in D Dorian — the medieval
mode — cross-fade with the situation: calm while you build, tension the moment
something of theirs is in sight, and a faster, drum-driven battle track (dropping
to Aeolian, one flattened note darker) while blows are landing. It settles back a
few seconds after a fight rather than snapping, so a lull in a skirmish doesn't
make the score stutter. Because the mood is read from what *you* can see, it never
tells you about an enemy before the fog would have. Every track loops without a
seam: notes that run past the end wrap around to the beginning, so a phrase
finishes over the top of the repeat. `N` turns it off.

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
dotnet run --project tests/Audio -- --write ./sfx    # dump every effect AND track as .wav
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
Fog of war is enforced in the simulation, so it gates orders rather than merely
dimming the screen, and what each player has explored is checksummed and survives
a rejoin. Phase 2 (the full RTS core) and Phase 3's pillars (walls, gatehouses,
siege, and the custom point-buy unit roster) are complete. See
`CONTEXT_HANDOFF.md`.
