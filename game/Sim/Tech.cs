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
        const int CrossBranchPenalty = 8;    // added per already-taken node outside a node's branch
        const int CapstoneLimit = 1;         // capstones you may hold — one branch's HIGH, no more

        // How much research a realm banks each realm tick.
        public int ResearchPace(int owner)
        {
            int pace = BaseResearchPace;
            if (IsTechResearched(owner, TechTree.Roads)) pace += RoadsPace;
            return pace;
        }

        public int ResearchPoints(int owner) => _stock.TryGetValue(owner, out var s) ? s[ResearchIdx] : 0;

        // Setup / test helper: bank research points directly, like AddGold.
        public void AddResearch(int owner, int amount) => StockOf(owner)[ResearchIdx] = Math.Max(0, ResearchPoints(owner) + amount);

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
