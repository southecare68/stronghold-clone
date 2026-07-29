using System;
using System.Collections.Generic;

namespace Sim
{
    // The four scored races to victory (docs/victory-paths.md). The whole design is
    // economy-primary: military is a shared TOOL every path uses, not a fifth path,
    // so the warlord fantasy lives inside Domain-by-force. Order is fixed and hashed
    // as an int, so it must never be reordered.
    public enum VictoryPath { Economic = 0, Religious = 1, Domain = 2, Science = 3 }

    public enum VictoryEventKind { Approaching = 0, Won = 1 }

    // One realm-wide announcement: a rival crossed 80% of a HIGH goal (Approaching),
    // or someone claimed a crown (Won). Transient UI signal, not hashed state — both
    // machines derive the same events from the same state and each shows its own
    // toast. The latch that stops a re-announce lives in the stock array and IS
    // hashed, so this list can be drained freely.
    public readonly struct VictoryEvent
    {
        public readonly VictoryEventKind Kind;
        public readonly int Owner;
        public readonly VictoryPath Path;
        public VictoryEvent(VictoryEventKind kind, int owner, VictoryPath path)
        { Kind = kind; Owner = owner; Path = path; }
    }

    // Everything the HUD needs to draw one owner's standing on one path, and
    // everything ResolveVictory needs to judge it. Progress toward the HIGH goal is a
    // 0..100 percent (what trips the 80% announce); the hold counter is how long that
    // goal has been HELD, which is what actually wins.
    public readonly struct VictoryProgress
    {
        public readonly bool HighMet;       // satisfies the HIGH goal right now
        public readonly bool MediumMet;     // satisfies the MEDIUM goal right now
        public readonly int HighPercent;    // 0..100 toward HIGH
        public readonly int HoldTicks;      // consecutive ticks HIGH has been held
        public readonly int HoldNeeded;     // ticks HIGH must be held to claim the crown
        public readonly bool MediumBanked;  // MEDIUM has been earned at least once (sticky)
        public readonly bool Announced;     // the realm has been told of this owner's 80%
        public VictoryProgress(bool highMet, bool mediumMet, int highPercent,
                               int holdTicks, int holdNeeded, bool mediumBanked, bool announced)
        {
            HighMet = highMet; MediumMet = mediumMet; HighPercent = highPercent;
            HoldTicks = holdTicks; HoldNeeded = holdNeeded; MediumBanked = mediumBanked; Announced = announced;
        }
    }

    public sealed partial class Simulation
    {
        // --- Goal thresholds (docs/victory-paths.md) --------------------------
        // Economic — the merchant's hoard: hold a million, having once banked half.
        const long EconHighGold = 1_000_000, EconMedGold = 500_000;
        // Religious — the flock: convert three-quarters of your people, then half.
        // (The design's "+ the faith of N other territories" clause reads through
        // TerritoryCount and is dormant until multi-territory ships — Phase 3.)
        const int RelHighFaith = 75, RelMedFaith = 50;
        // Domain — the census: a great population across many keeps.
        const int DomHighPop = 5000, DomMedPop = 2500;
        const int DomHighTerr = 5, DomMedTerr = 2;
        // Science — the tech tree + wonders. The Academy capstone completes the branch
        // and unlocks Wonders; the HIGH is two of them, the MEDIUM one. (The design's
        // "complete the tree" is subsumed by the Academy gate — the capstone's prereq
        // chain IS the whole branch.)
        const int SciHighWonders = 2, SciMedWonders = 1;

        // Cross this share of a HIGH goal and the whole realm is told — the window in
        // which spies and raids can still bite. A single dial for every path.
        public const int AnnounceAt = 80;

        // Sim tick rate: RealmInterval (40 ticks) is "2s", so 20 ticks a second.
        const int TickRate = 20;
        // How long a HIGH goal must be HELD, not merely touched, before it counts —
        // the design's sustained-hold windows (~10-30 real minutes), in ticks. Public
        // so the render layer can show a countdown and tests can drive it exactly. The
        // hoard is the longest vigil; the rest share ten minutes.
        public static int HoldTicksFor(VictoryPath path) => path switch
        {
            VictoryPath.Economic => 30 * 60 * TickRate,   // a million held for 30 min
            _                    => 10 * 60 * TickRate,    // the others, ~10 min
        };

        // Realm-wide announcements produced this match, oldest first. Transient (not
        // hashed, not snapshotted) — the render layer drains it into toasts.
        readonly List<VictoryEvent> _victoryEvents = new();
        public IReadOnlyList<VictoryEvent> VictoryEvents => _victoryEvents;
        public void ClearVictoryEvents() => _victoryEvents.Clear();

        // A realm's territory count — the seam multi-territory (Phase 3) grows into.
        // Today an owner holds exactly one home territory, anchored on its keep, so
        // this counts live keeps and returns 1 in every current match. Domain's "5
        // territories" and Religion's "2 other territories" clauses score THROUGH
        // here, so they light up the moment conquest starts minting extra keeps —
        // without the scoring code below changing at all.
        public int TerritoryCount(int owner)
        {
            int n = 0;
            foreach (var b in Buildings) if (b.Alive && b.Owner == owner && b.Type == BuildingType.Keep) n++;
            return n;
        }

        static int Pct(long v, long goal) => goal <= 0 ? 0 : (int)Math.Clamp(v * 100 / goal, 0, 100);

        // One owner's live standing on one path: the HIGH/MEDIUM tests against the
        // current metric, plus the hold counter and latches read back from the stock
        // array. Pure read — safe for the HUD to call every frame.
        public VictoryProgress Progress(int owner, VictoryPath path)
        {
            _stock.TryGetValue(owner, out var s);
            int pi = (int)path;
            int hold = s != null ? s[VicHoldBase + pi] : 0;
            bool banked = s != null && s[VicMedBase + pi] != 0;
            bool announced = s != null && s[VicAnnBase + pi] != 0;

            bool highMet, medMet; int highPct;
            switch (path)
            {
                case VictoryPath.Economic:
                {
                    long gold = Gold(owner);
                    highMet = gold >= EconHighGold; medMet = gold >= EconMedGold;
                    highPct = Pct(gold, EconHighGold);
                    break;
                }
                case VictoryPath.Religious:
                {
                    int f = Faith(owner);
                    highMet = f >= RelHighFaith; medMet = f >= RelMedFaith;
                    highPct = Pct(f, RelHighFaith);
                    break;
                }
                case VictoryPath.Domain:
                {
                    int pop = PeasantCount(owner), terr = TerritoryCount(owner);
                    highMet = pop >= DomHighPop && terr >= DomHighTerr;
                    medMet  = pop >= DomMedPop  && terr >= DomMedTerr;
                    highPct = Pct(pop, DomHighPop);   // the pop bar; the territory gate is a hard AND above
                    break;
                }
                case VictoryPath.Science:
                {
                    int wonders = WonderCount(owner);
                    int tree = ResearchedCount(owner, TechBranch.Science);   // 0..4 nodes on a path
                    highMet = IsTechResearched(owner, TechTree.Academy) && wonders >= SciHighWonders;
                    medMet = wonders >= SciMedWonders;
                    // The bar fills as the branch is researched (up to ~56) and jumps
                    // with each wonder (+30) — full tree + one wonder lands near 80%,
                    // the announce, with the second wonder the crown.
                    highPct = Math.Clamp(tree * 14 + wonders * 30, 0, 100);
                    break;
                }
                default:
                    highMet = false; medMet = false; highPct = 0;
                    break;
            }

            // Capstone gate: the tech tree IS the victory structure, so a HIGH goal
            // only counts once its branch capstone is researched. A branch with no
            // capstone defined yet (Economic/Domain/Science, until ported) is ungated,
            // so its HIGH stays metric-only for now.
            int capstone = TechTree.CapstoneFor(TechTree.BranchOf(path));
            if (capstone >= 0 && !IsTechResearched(owner, capstone)) highMet = false;

            return new VictoryProgress(highMet, medMet, highPct, hold, HoldTicksFor(path), banked, announced);
        }

        // Score every realm on every path, fire the 80% announcements, advance the
        // sustained-hold counters, and award the crown to anyone who has held a HIGH
        // goal to term while banking a MEDIUM on a DIFFERENT path. Runs on the realm
        // cadence, right after ResolveRealm, so it reads freshly-settled metrics. A
        // match with no keep (the units-only parity scenario) does nothing here.
        void ResolveVictory()
        {
            if (TickNumber == 0 || TickNumber % RealmInterval != 0) return;
            if (VictoryOwner >= 0) return;   // a crown is already claimed — the match is decided

            var realms = new SortedSet<int>();
            foreach (var b in Buildings) if (b.Alive && b.Type == BuildingType.Keep) realms.Add(b.Owner);

            foreach (int owner in realms)          // owner order — deterministic
            {
                var s = StockOf(owner);
                for (int pi = 0; pi < PathCount; pi++)
                {
                    var prog = Progress(owner, (VictoryPath)pi);

                    // MEDIUM banks forever the first time it is earned — the second
                    // half of every dual goal, and (for the merchant) a "once" goal.
                    if (prog.MediumMet && s[VicMedBase + pi] == 0) s[VicMedBase + pi] = 1;

                    // Cross 80% of a HIGH goal and the whole realm hears of it, once.
                    if (prog.HighPercent >= AnnounceAt && s[VicAnnBase + pi] == 0)
                    {
                        s[VicAnnBase + pi] = 1;
                        _victoryEvents.Add(new VictoryEvent(VictoryEventKind.Approaching, owner, (VictoryPath)pi));
                    }

                    // The hold accrues while the HIGH goal holds and resets the instant
                    // it lapses — a goal must be SUSTAINED, never merely snapped shut.
                    s[VicHoldBase + pi] = prog.HighMet
                        ? Math.Min(prog.HoldNeeded, s[VicHoldBase + pi] + RealmInterval)
                        : 0;
                }

                // Dual goal: a HIGH held to term on one path + a MEDIUM banked on ANY
                // other. No single-stat crown — every winner has touched two paths.
                for (int a = 0; a < PathCount; a++)
                {
                    if (s[VicHoldBase + a] < HoldTicksFor((VictoryPath)a)) continue;
                    for (int b = 0; b < PathCount; b++)
                    {
                        if (b == a || s[VicMedBase + b] == 0) continue;
                        DeclareVictory(owner, a);
                        return;
                    }
                }
            }

            // The buzzer. If a match clock was set and no one claimed a crown by it,
            // the realm furthest along across all paths takes it; owner order breaks
            // ties. Off (0) by default, so an untimed match simply plays on.
            if (MatchClockTicks > 0 && TickNumber >= MatchClockTicks)
            {
                int best = -1, bestScore = -1, bestPath = 0;
                foreach (int owner in realms)
                {
                    int score = 0, top = -1, topPath = 0;
                    for (int pi = 0; pi < PathCount; pi++)
                    {
                        int pct = Progress(owner, (VictoryPath)pi).HighPercent;
                        score += pct;
                        if (pct > top) { top = pct; topPath = pi; }
                    }
                    if (score > bestScore) { bestScore = score; best = owner; bestPath = topPath; }
                }
                if (best >= 0) DeclareVictory(best, bestPath);
            }
        }

        void DeclareVictory(int owner, int pathIdx)
        {
            VictoryOwner = owner;
            VictoryPathIdx = pathIdx;
            _victoryEvents.Add(new VictoryEvent(VictoryEventKind.Won, owner, (VictoryPath)pathIdx));
        }
    }
}
