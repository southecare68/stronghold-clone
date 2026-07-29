using System;

namespace Sim
{
    // The research economy and the rules for climbing the tech web (TechTree.cs).
    // Research points bank every realm tick and are spent, one node at a time,
    // through a Research command. Prereqs and forks shape the branch; a pick-limit
    // on capstones and an escalating cross-branch cost are what force the dual-goal
    // — you can afford one branch to its capstone plus a shallow dip into a second,
    // never two capstones. All integer, all on the stock array, so it stays
    // deterministic and rides the snapshot for free.
    public sealed partial class Simulation
    {
        const int BaseResearchPace = 3;      // research points per realm tick, before boosts
        const int RoadsPace = 3;             // Roads (trunk) speeds every realm's research
        const int LibraryPace = 3;           // the Science branch is a research multiplier on itself
        const int ScholarshipPace = 4;       // Scholarship fork: faster tree
        const int PrintingPace = 4;          // Printing Press: speeds tech
        const int CrossBranchPenalty = 8;    // added per already-taken node outside a node's branch
        const int CapstoneLimit = 1;         // capstones you may hold — one branch's HIGH, no more

        // How much research a realm banks each realm tick — the base, plus every
        // research-speed node it has taken (Roads, then the Science branch, which is a
        // research multiplier on itself).
        public int ResearchPace(int owner)
        {
            int pace = BaseResearchPace;
            if (IsTechResearched(owner, TechTree.Roads)) pace += RoadsPace;
            if (IsTechResearched(owner, TechTree.Library)) pace += LibraryPace;
            if (IsTechResearched(owner, TechTree.Scholarship)) pace += ScholarshipPace;
            if (IsTechResearched(owner, TechTree.PrintingPress)) pace += PrintingPace;
            return pace;
        }

        // Live wonders an owner holds — the Science path's tangible metric.
        public int WonderCount(int owner) => CountBuildings(owner, BuildingType.Wonder);

        // How many nodes of a branch this owner has researched — the tree-progress a
        // branch's HUD bar reads.
        public int ResearchedCount(int owner, TechBranch branch)
        {
            int n = 0;
            foreach (var node in TechTree.All())
                if (node.Branch == branch && IsTechResearched(owner, node.Id)) n++;
            return n;
        }

        // What a build costs THIS owner right now. Only wonders differ from the flat
        // table: each one you already hold makes the next dearer (the design's
        // escalating cost — #2 is the real commitment), and Engineering shaves a
        // quarter off. Public so the palette shows the true next-wonder price.
        public int[] BuildCostFor(int owner, BuildingType type)
        {
            var b = BuildCost[(int)type];
            if (type != BuildingType.Wonder) return b;
            int mult = 1 + WonderCount(owner);                 // 1st ×1, 2nd ×2 …
            int num = IsTechResearched(owner, TechTree.Engineering) ? 3 : 4;   // Engineering: ×3/4
            return new[] { b[0] * mult * num / 4, b[1] * mult * num / 4, b[2] * mult * num / 4 };
        }

        public int ResearchPoints(int owner) => _stock.TryGetValue(owner, out var s) ? s[ResearchIdx] : 0;

        // Setup / test helper: bank research points directly, like AddGold.
        public void AddResearch(int owner, int amount) => StockOf(owner)[ResearchIdx] = Math.Max(0, ResearchPoints(owner) + amount);

        // --- Economic branch: gold that isn't taxed from peasants ----------------
        // The Economic branch is a gold engine — trade FLOW on top of tax, which is
        // what carries a realm to the half-million and then holds the million.
        const int TradePostGold = 5;         // caravans: a steady flow each realm tick
        const int MonopolyGold = 12;         // one good, high margin — a fat flat rate
        const int BourseGoldPerBld = 1;      // diversified, resilient — scales with a broad economy
        const int InterestDivisor = 200;     // Banking House: compound interest, ~0.5% of the hoard/tick
        const int InterestCap = 300;         // capped so it can't run away...
        const int InterestCapGrand = 600;    // ...unless the Grand Exchange raises the ceiling
        const int GrandExchangeFloor = 25;   // and it guarantees an income floor, to sustain the hold

        int LiveBuildingCount(int owner)
        {
            int n = 0;
            foreach (var b in Buildings) if (b.Alive && b.Owner == owner) n++;
            return n;
        }

        // Gold the Economic tech web pays this realm each realm tick, on top of tax.
        // Public so the HUD can show the trade income the branch is earning.
        public int EconomicIncome(int owner)
        {
            int g = 0;
            if (IsTechResearched(owner, TechTree.TradePost)) g += TradePostGold;
            if (IsTechResearched(owner, TechTree.Monopoly)) g += MonopolyGold;
            if (IsTechResearched(owner, TechTree.Bourse)) g += BourseGoldPerBld * LiveBuildingCount(owner);
            if (IsTechResearched(owner, TechTree.BankingHouse))
            {
                int cap = IsTechResearched(owner, TechTree.GrandExchange) ? InterestCapGrand : InterestCap;
                g += Math.Min(Gold(owner) / InterestDivisor, cap);
            }
            if (IsTechResearched(owner, TechTree.GrandExchange)) g = Math.Max(g, GrandExchangeFloor);
            return g;
        }

        // Is this node in this owner's researched set? Bit `id` of the tech mask.
        public bool IsTechResearched(int owner, int id)
        {
            if (id < 0 || !_stock.TryGetValue(owner, out var s)) return false;
            int word = TechBase + id / 32, bit = id % 32;
            if (word >= TechBase + TechWords) return false;
            return (s[word] & (1 << bit)) != 0;
        }

        void SetTechResearched(int owner, int id)
        {
            var s = StockOf(owner);
            int word = TechBase + id / 32, bit = id % 32;
            if (word < TechBase + TechWords) s[word] |= (1 << bit);
        }

        static bool IsScoredBranch(TechBranch b) =>
            b == TechBranch.Economic || b == TechBranch.Religious || b == TechBranch.Science || b == TechBranch.Domain;

        // Researched nodes in scored branches other than `branch` — what the
        // cross-branch penalty scales with, so a second branch gets pricier the
        // deeper you have already gone in your first.
        int OffBranchNodeCount(int owner, TechBranch branch)
        {
            int n = 0;
            foreach (var node in TechTree.All())
                if (IsScoredBranch(node.Branch) && node.Branch != branch && IsTechResearched(owner, node.Id)) n++;
            return n;
        }

        // The price of a node right now: its base plus the cross-branch penalty for a
        // scored-branch node (trunk and war nodes pay base only).
        public int ResearchCostFor(int owner, int id)
        {
            if (!TechTree.Exists(id)) return int.MaxValue;
            var node = TechTree.Node(id);
            int cost = node.BaseCost;
            if (IsScoredBranch(node.Branch)) cost += CrossBranchPenalty * OffBranchNodeCount(owner, node.Branch);
            return cost;
        }

        int CapstonesHeld(int owner)
        {
            int n = 0;
            foreach (var node in TechTree.All())
                if (node.IsCapstone && IsTechResearched(owner, node.Id)) n++;
            return n;
        }

        // Has this owner already taken some node in a given fork group?
        bool ForkGroupTaken(int owner, int group)
        {
            if (group == 0) return false;
            foreach (var node in TechTree.All())
                if (node.ForkGroup == group && IsTechResearched(owner, node.Id)) return true;
            return false;
        }

        // May this owner research this node right now? Everything the rules demand:
        // it exists and is new, its prereqs are all taken, its tier's fork (if any) is
        // satisfied, no sibling fork is already picked, the capstone pick-limit is
        // respected, and there are enough banked points.
        public bool CanResearch(int owner, int id)
        {
            if (!TechTree.Exists(id) || IsTechResearched(owner, id)) return false;
            var node = TechTree.Node(id);

            foreach (int pre in node.Prereqs) if (!IsTechResearched(owner, pre)) return false;
            if (node.RequiresFork != 0 && !ForkGroupTaken(owner, node.RequiresFork)) return false;
            if (node.ForkGroup != 0 && ForkGroupTaken(owner, node.ForkGroup)) return false;   // a sibling closed it
            if (node.IsCapstone && CapstonesHeld(owner) >= CapstoneLimit) return false;

            return ResearchPoints(owner) >= ResearchCostFor(owner, id);
        }

        // A node's standing for the HUD — the same checks as CanResearch, but split
        // so the panel can tell "you can't afford it yet" from "it's locked" from "a
        // fork closed it". Available here is exactly CanResearch == true.
        public TechState TechStateOf(int owner, int id)
        {
            if (!TechTree.Exists(id)) return TechState.Locked;
            if (IsTechResearched(owner, id)) return TechState.Researched;
            var node = TechTree.Node(id);
            if (node.ForkGroup != 0 && ForkGroupTaken(owner, node.ForkGroup)) return TechState.Closed;
            foreach (int pre in node.Prereqs) if (!IsTechResearched(owner, pre)) return TechState.Locked;
            if (node.RequiresFork != 0 && !ForkGroupTaken(owner, node.RequiresFork)) return TechState.Locked;
            if (node.IsCapstone && CapstonesHeld(owner) >= CapstoneLimit) return TechState.Locked;
            return ResearchPoints(owner) >= ResearchCostFor(owner, id) ? TechState.Available : TechState.Unaffordable;
        }

        // Spend on a node if it is legal — the one mutation, behind every check above.
        // Returns whether it happened (the command path ignores the result; tests and
        // the AI read it).
        public bool TryResearch(int owner, int id)
        {
            if (!CanResearch(owner, id)) return false;
            StockOf(owner)[ResearchIdx] -= ResearchCostFor(owner, id);
            SetTechResearched(owner, id);
            return true;
        }
    }
}
