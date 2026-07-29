# Victory paths, spies, and the road there

This is the design of record for how a match is *won*. It consolidates the two
design docs (`victorypaths.pdf`, `spy.pdf`) into a build plan grounded in the
sim we already have, and records the decisions that shape near-term work.

The guiding call: **keep the foundations, and let the paths guide sequencing.**
The whole design is economy-primary — every path is a race along the same
economic axes (tall/wide, hoard/flow, extract/sustain), so the deterministic
economy sim *is* the engine the paths race on. The paths are scoring lenses laid
over that engine, plus a referee that watches the meters. Nothing about the
food/gold/popularity/territory systems is thrown away.

---

## How you win

Achieve the **HIGH** goal of one path **and** the **MEDIUM** goal of a *different*
path. No single-stat cheese — every winner has touched two paths. With four
players filling two path-slots each, every path is contested roughly twice.

### The four paths

| Path | Crown | HIGH | MEDIUM |
|---|---|---|---|
| **Economic** | The Merchant | Hold **1,000,000 gold** for 30 min | Bank **500,000 gold** once |
| **Religious** | The Faithful | Convert **75%** of your people (+ faith of 2 other territories) | **50%** conversion (+ 1 other territory) |
| **Domain** | The Sovereign | **5,000 population** + hold **5 territories** | **2,500 pop** + **2 territories** |
| **Science** | The Scholar | Complete the tech tree + **2 wonders** | 1 wonder + 75% of the tree |

### The shared laws (what keeps it balanced)

- **Dual goal** — high in one path + medium in another.
- **Everything is announced** — cross ~80% of any HIGH goal and the whole realm
  is told, opening the counterplay window.
- **Sustained-hold windows** — every HIGH goal must be *held* for a set time, not
  snapped shut, so spies and raids have something to bite.
- **One clock** — a single match timer decides the winner at the buzzer; its
  length is the master dial (short favors tempo, long favors scaling).
- **Diminishing returns** — over-investing one track has a falling ceiling, so
  hybrids stay attractive.
- **War feeds the attacker** — every act of war advances your own path (loot /
  annex / sabotage); no spite-only griefing. Military is a shared **tool**, not a
  fifth path.

### The spy counter-web

Two tiers. One dagger per crown, plus one for the shared sword. A spy is never
generic harassment — it is the specific answer to one way of winning, or to the
one tool everyone shares.

| Spy | Counters | Offense |
|---|---|---|
| The Embezzler | Economic | Skims gold from the announced hoard into yours |
| The Inquisitor | Religious | Discredits & re-converts a slice of the flock, dropping their % (**the only thing that pushes conversion backward**) |
| The Saboteur | Science | Wrecks a wonder's progress or steals a researched tech |
| The Agitator | Domain | Incites emigration — peasants drift toward *your* land |
| The Assassin | **War itself** | Kills the commander of a raid/invasion/garrison, stalling the attack |

---

## Implementation status

### Shipped — the victory spine + faith (Phase 0 + 1)

The referee and the first scored metric are live in the sim and fully tested
(`tests/Victory`). Deterministic, snapshotted, and checksummed like everything
else; the frozen units-only parity constant (`0xB1A7A676`) is untouched.

- **Faith** (`FaithIdx` on the per-owner stock array). Opens at a starting
  congregation (`StartFaith` 25%), rests there with no church (`BaseFaith`), and
  drifts toward the share a realm's churches can reach — each `Church` ministers
  to `ChurchSeats` peasants; when total reach covers the populace, faith climbs
  toward 100%. Reversible (let population outrun your churches and it slips back),
  which is the seam the Inquisitor plugs into. Conversion settles on the
  popularity cadence in `ResolveRealm`. **Not** coupled to approval yet — that's
  an available tuning lever, deliberately left off so the metric stays clean.
- **The Church building** (`BuildingType.Church`) — timber + stone, passive
  infrastructure (no worker), the only thing that raises faith.
- **The spine** (`game/Sim/Victory.cs`): `VictoryPath` enum, per-path HIGH/MEDIUM
  thresholds, a pure `Progress(owner, path)` read for the HUD, per-owner
  hold-timers + medium/announce latches (all on the stock array), the 80%
  announcement (`VictoryEvents`), the dual-goal win check, and an optional match
  clock (`MatchClockTicks`, 0 = off). All four paths are wired; the metrics that
  exist today are gold (Economic), faith (Religious), and population (Domain).

### Wired but dormant (waiting on later phases)

- **Territories.** `TerritoryCount(owner)` counts live keeps — 1 in every current
  match. Domain's "5 territories" and Religion's "2 other territories" clauses
  already score *through* it, so they light up the moment conquest starts minting
  extra keeps, with no change to the scoring code. **This is the multi-territory
  seam** (see the decision below).
- **Science** is a wired stub: a fourth path slot returning zero progress until
  the tech-tree and wonder systems exist (Phase 4).

---

## Decision: the multi-territory model (design now, build later)

Two paths depend on holding more than one territory, but our world today is one
home territory per player. Rather than build conquest now or stub the goals away,
we **fix the shape now and implement it in Phase 3**:

- **1 keep = 1 territory.** A territory is the region anchored on a keep (the same
  anchor `HomeRect` already uses — which is why the border stays put as you build
  inside it). An owner holds as many territories as they have live keeps.
- **`TerritoryCount` is the single seam.** Every cross-territory clause reads
  through it. Growing an owner past one keep — by building a second keep in
  unclaimed land, or by **annexing** a defeated rival's keep — automatically makes
  Domain's and Religion's multi-territory goals reachable.
- **Faith stays per-realm for now**, aggregated at the owner. When territories
  become plural, faith becomes per-territory and Religion's "+ the faith of N
  other territories" reads the neighbors' shares. The `Faith(owner)` API is the
  aggregation point that will grow a territory argument.
- **What Phase 3 actually builds:** a `Territory` record keyed by keep; conquest
  that transfers a keep's ownership (and its territory, population, and buildings);
  `HomeRect` generalized from "merge all my keeps into one box" to "one box per
  keep." No scoring code changes — only the world underneath it.

---

## Roadmap

Ordered by how much foundation already exists. Each phase is a plug-in to the
spine, not a rewrite.

| Phase | Scope | Status |
|---|---|---|
| **0** | Victory spine — metrics, 80% announce, hold-timers, match clock | ✅ shipped |
| **1** | Faith as a scored, reversible metric + the Church | ✅ shipped |
| **2** | Economic scoring — a **market** for gold *velocity* + the 1M hoard on the HUD | next |
| **3** | **Multi-territory** + Domain census + conquest/annexation | designed (above) |
| **4** | Science — tech tree + wonders (visible, sabotageable) | greenfield |
| **5** | War-as-tool (raid / loot / annex / sabotage) + the five spies | last — spies need a live, announced metric to bite |

Spies land last on purpose: an Embezzler with no announced hoard, or an
Inquisitor with no conversion score, has nothing to counter. Build at least two
scored metrics first, then the daggers.

### Reserve (first expansion)

**Splendor / Beauty** — the "beautiful castle" fantasy, a genuinely different
scoring shape (adjacency & symmetry), countered by an Arsonist.

---

## Tuning knobs (all in one place)

- Faith: `StartFaith`, `BaseFaith`, `ChurchSeats`, `ConvertRate` (`Simulation.cs`).
- Goals: `EconHighGold`/`EconMedGold`, `RelHighFaith`/`RelMedFaith`,
  `DomHighPop`/`DomMedPop`/`DomHighTerr`/`DomMedTerr` (`Victory.cs`).
- Windows: `AnnounceAt` (80%), `HoldTicksFor(path)`, `MatchClockTicks`
  (`Victory.cs`). Hold windows are in ticks (`TickRate` = 20/s) so they express
  the design's ~10–30 real minutes and tests can drive them exactly.
