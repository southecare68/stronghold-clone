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

*(These are the design's numbers. The shipped targets are scaled to the prototype so
the four races are comparable — see [Balance](#balance).)*

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

### The tech tree — the victory structure (shipped: spine + all four branches + the spy web + HUD)

Per `tech.pdf`, **the tree *is* the victory structure**: the four paths are branches
of one research web, and a branch's **capstone is what unlocks its HIGH goal**. The
spine and the Religious branch are in and tested (`tests/Tech`, `game/Sim/TechTree.cs`
+ `Tech.cs`):

- **Research points** (`ResearchIdx`) bank every realm tick at a `ResearchPace`
  (base + Roads), on the stock array like faith/gold.
- **The web** (`TechTree.cs`): a shared trunk (Roads → Chapel/Market unlocks) and
  the full Religious branch — Shrine → the Holy Order **fork** (Missionaries |
  Zealotry, pick one) → Cathedral → **★ Grand Temple** capstone. Nodes carry
  prereqs, fork groups, and a capstone flag; the researched set is a 128-bit mask
  on the stock array.
- **The dual-goal, as economics**: an **escalating cross-branch cost**
  (`CrossBranchPenalty` per off-branch node) makes a second branch dear, and a
  **capstone pick-limit** (`CapstoneLimit = 1`) means you can only capstone *one*
  branch — so "depth in one + a dip into a second" is enforced by the tree itself.
- **Capstone-gates-HIGH**: `Victory.Progress()` now requires a branch's capstone
  (`TechTree.CapstoneFor`) before its HIGH goal counts. **75% faith no longer wins
  on its own — you must research the Grand Temple.** Branches with no capstone yet
  (Economic/Domain/Science) stay metric-only until ported.
- **Research command** (`CommandType.Research`) takes through the normal charged,
  validated path; the **AI is path-aware** — `EnableAi(owner, level, path)` assigns
  a crown (default Religious, so `AiSim` is unchanged), and the bot climbs *that*
  branch to its capstone and raises its structures: churches, wonders, or new keeps.
  A match with several bots spreads them across the crowns (by owner id). It also
  researches its branch's **⚔ war-tool**, so its fights feed its crown; a **Hard**
  bot additionally climbs the **Spy Guild**, funds espionage with an offsetting tax,
  and looses the dagger that answers its rival's leading path. It also **defends**:
  each path's own counter (Cathedral / Banking House / Printing Press / Provincial
  Keeps) already rides the climb to that capstone, so the one shield it reaches for
  specially is the **Bodyguard**, rushed the moment a rival trains the Assassin. And
  it **reacts to the 80% announcements**: once a rival is announced on a crown (the
  latched `Progress().Announced`), the bot locks its counter-dagger onto *that* path
  and keeps it suppressed, over the merely-leading heuristic. Proven in
  `tests/AiPaths` — a Hard bot reaches every capstone and drives every metric,
  climbs the Spy Guild to fire a dagger, rushes the Bodyguard when threatened, and
  focuses the Inquisitor on a rival announced near the faith crown.
- **Economic branch** (`EconomicIncome`): a gold engine on top of tax — Trade Post
  pays a steady flow, the Guild Charter fork adds Monopoly's flat high margin *or*
  Bourse's per-building breadth, Banking House compounds interest on the hoard, and
  the **Grand Exchange** capstone floors the income and unlocks the gold HIGH. Added
  to gold each realm tick; a **Tech HUD** (C key) lets a human spend research, node
  by node, across every branch.
- **Science branch**: Scholar's Hut → Library → the University fork (Engineering =
  cheaper wonders | Scholarship = faster tree) → Printing Press → the **Academy**
  capstone, which unlocks **Wonders** (`BuildingType.Wonder`) — a grand, expensive
  buildable, gated on the Academy, whose cost **escalates** with each one standing
  (`BuildCostFor`). A wonder **rises over `WonderBuildTime`** (`Construction` on the
  building): it stands visible — and **sabotageable** — while it builds, and only
  counts toward the crown once finished (`WonderCount` = complete only). It visibly
  emerges from the ground as it nears done. The Science HIGH is **two finished
  wonders** (the MED is one); research nodes along the way quicken `ResearchPace`.
- **Domain branch + multi-territory**: Farmstead → Husbandry → the Settlement fork
  (Homesteads = ×4 housing | Colonists = faster growth) → **Provincial Keeps** →
  the **Sovereign's Court** capstone. Provincial Keeps lets you **found a new keep**
  through the build palette — each keep is a new **territory** (`TerritoryCount`),
  costs dear, and must sit `KeepSpacing` clear of your others (a real territory, not
  a cluster); the first keep keeps its drop-off so the original economy holds.
  Homesteads multiplies `PopulationCap`, Husbandry/Colonists add arrivals while a
  realm grows. The Domain HIGH is **population + 5 territories** (capstone-gated).
  *Peaceful expansion only for now — conquest/annexation (taking a territory by
  force) lands with the war layer.*
- **The spy counter-web** (`Spy.cs`, War branch): Muster → Spy Guild → the five
  spies + Bodyguard. A spy is trained by research, costs gold, sits on a cooldown,
  and is fired at your rival through a `Spy` command (`FirstRival` auto-targets the
  lone enemy). Each is the ONE thing that pushes a rival's metric backward —
  **Embezzler** skims the hoard, **Inquisitor** knocks faith down, **Saboteur**
  wrecks a wonder, **Agitator** incites emigration, **Assassin** cuts down a
  soldier — and each is blunted by the target's own **Tier-III counter** (Banking
  House / Cathedral / Printing Press / Provincial Keeps), the Bodyguard blocking the
  Assassin outright. The War branch is a shared tool (no capstone, no cross-branch
  penalty). A **Spies HUD** (X key) fires them; cooldowns and gates show on each.
- **War-tool payoffs** (`WarPayoff`) — "war feeds the attacker". Each branch's ⚔
  node turns an enemy your soldiers cut down into fuel for that path: **Privateers**
  (Economic) pillage gold into your hoard, **War Loot** (Science) strip wood & stone
  to fund wonders, a **Crusade** (Religious) emboldens the faith. Hooked at both
  combat kill-sites; zero unless researched, so a match with no war-tool tech plays
  exactly as before. **Conquest** (Domain) is the fourth — it takes a whole keep 👇.
- **Conquest/annexation** (`AnnexKeep`, Domain war-tool node `Conquest`): once
  Conquest is researched, a keep your army strikes down is **annexed, not razed** —
  it stands battered under its new lord, becomes a new **territory** (feeding
  Domain's "5 territories" *by force*), and the old owner's **idle folk within
  `AnnexRadius`** change hands (the population payoff). Without the tech a felled
  keep falls as before, so existing sieges are untouched. Units stop battering a
  keep the moment it turns friendly; an "⚔ seized a territory" toast fires. This is
  Domain's "or taken by force" — the aggressive player's road to the census crown.

### Wired but dormant (waiting on later phases)

- **Territories.** `TerritoryCount(owner)` counts live keeps — 1 in every current
  match. Domain's "5 territories" and Religion's "2 other territories" clauses
  already score *through* it, so they light up the moment conquest starts minting
  extra keeps, with no change to the scoring code. **This is the multi-territory
  seam** (see the decision below).
- **Science** is a wired stub: a fourth path slot returning zero progress until
  its tech branch (Library → University → Academy → Wonders) is filled in.

---

## Decision: the multi-territory model (peaceful expansion shipped; conquest next)

> **Status:** the seam below is now live **both ways** — a Domain player founds new
> keeps (each a territory) once Provincial Keeps is researched, **and** annexes an
> enemy's keep by force once Conquest is researched (see the conquest note below).

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

The tech tree reframes the roadmap: the remaining work is **filling in branches**
on the shipped tech spine. Each is a plug-in, not a rewrite.

| Phase | Scope | Status |
|---|---|---|
| **0** | Victory spine — metrics, 80% announce, hold-timers, match clock | ✅ shipped |
| **1** | Faith metric + the Church | ✅ shipped |
| **T** | **Tech spine** — research economy, the web, capstone-gates-HIGH, cross-branch cost + **Religious branch** ported, AI climbs it | ✅ shipped |
| **T-ui** | Tech-tree HUD — research readout, a node panel to spend it, capstone/branch progress | ✅ shipped |
| **2** | **Economic branch** — Trade Post → Guild Charter fork → Banking House → **★ Grand Exchange**, paying a trade income (flow, margin, interest) on top of tax; capstone gates the gold HIGH | ✅ shipped |
| **4** | **Science branch** — Scholar's Hut → Library → University fork → Academy → **Wonders** (the new buildable); HIGH = 2 wonders, MED = 1, capstone-gated | ✅ shipped |
| **3** | **Domain branch + multi-territory** — Farmstead → Husbandry → Settlement fork → Provincial Keeps → **★ Sovereign's Court**; **found new keeps** (each a territory), Homesteads multiplies housing, Husbandry/Colonists speed growth; HIGH = pop + 5 territories | ✅ shipped (peaceful expansion) |
| **5** | **The spy counter-web** — Muster → Spy Guild → the five spies + Bodyguard; each dagger pushes one rival metric back, blunted by that branch's Tier-III counter | ✅ shipped |
| **6** | **Conquest/annexation** — take a keep by force → its territory & population (Domain's ⚔ war-tool) | ✅ shipped |
| **7** | **The rest of the war-tool payoffs** — the other branches' ⚔ nodes (Privateers loot gold · War Loot funds wonders · Crusade emboldens the faith) | ✅ shipped |

The tech HUD comes next so a human can actually spend research (the AI already
does). Spies land last on purpose: an Embezzler with no announced hoard, or an
Inquisitor with no conversion score, has nothing to counter.

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
- Tech: `BaseResearchPace`, `RoadsPace`, `CrossBranchPenalty`, `CapstoneLimit`
  (`Tech.cs`); node costs and the branch shape in `TechTree.cs`.
- Economic income: `TradePostGold`, `MonopolyGold`, `BourseGoldPerBld`,
  `InterestDivisor`/`InterestCap`/`InterestCapGrand`, `GrandExchangeFloor`
  (`Tech.cs`), added to gold each realm tick in `ResolveRealm`.
- Science: research-pace nodes `LibraryPace`/`ScholarshipPace`/`PrintingPace`
  (`Tech.cs`); Wonder base cost in `BuildCost`, its escalation + Engineering
  discount in `BuildCostFor`; `WonderBuildTime` (the rise + sabotage window,
  `Simulation.cs`); `SciHighWonders`/`SciMedWonders` (`Victory.cs`).
- Domain: `HomesteadMult`, `KeepSpacing`, Keep build cost in `BuildCost`
  (`Simulation.cs`); `DomHighPop`/`DomMedPop`/`DomHighTerr`/`DomMedTerr`
  (`Victory.cs`) — the pop targets are scaled to the prototype (see the census
  note), the territory counts follow the design.
- Conquest: `AnnexRadius` (`Simulation.cs`) — how much population a taken keep
  carries; the `Conquest` node cost in `TechTree.cs`.
- War-tool payoffs: `PrivateerLoot` / `WarLootMat` / `CrusadeFaith` per kill
  (`Tech.cs`); the `Privateers` / `WarLoot` / `Crusade` node costs (`TechTree.cs`).
- Spies: `SpyCost`, `SpyCooldown`, and the per-effect sizes `EmbezzleCap` /
  `InquisitHit`+`InquisitSoft` / `SabotageHit`+`SabotageSoft` / `AgitateHit`+
  `AgitateSoft` (`Spy.cs`); the counters are the branches' own Tier-III nodes.

---

## Balance

`tests/Balance` is a **path-race harness**: it pursues each crown with a lone realm
on the *same* granted build economy (gold and population are NOT granted — they must
be earned and settled), and reports the tick at which each HIGH goal is first
reached. It asserts every path reaches its crown and the spread stays within a
factor, so a retune that breaks parity fails the suite.

The design's headline numbers (a million gold, five thousand souls) assume a far
larger-scale game; at this prototype's rates they'd make Economic and Domain
multi-hour marathons next to a two-minute Religious rush. Scaled to level the field,
the current reach times are:

| Path | Target | Reach |
|---|---|---|
| Science | Academy + 2 wonders | ~1.6 min |
| Religious | 75% faith | ~1.7 min |
| Domain | 180 pop + 5 territories | ~5.8 min |
| Economic | hold 70,000 gold | ~9.6 min |

Spread ~5.9× (was ~395× before tuning). The fast pair (Religious, Science) *reach*
quickly then defend a 10-minute hold against the Inquisitor / Saboteur; the slow
pair (Domain, Economic) build up. Hold windows: Economic 15 min, the rest 10 min.
Re-run `tests/Balance` after any change to a path's engine or target.
