using System;
using System.Collections.Generic;

namespace Sim
{
    // The four-path tech tree (docs/victory-paths.md, "The tree IS the victory
    // structure"). A shared trunk, one branch per scored path, and a cross-cutting
    // war layer. Depth in one branch to its capstone + a shallow dip into a second
    // is the dual-goal, expressed as research: the capstone is what ENABLES a path's
    // HIGH goal, and an escalating cross-branch cost is what stops you capstoning two.
    //
    // Trunk and War sit alongside the four VictoryPath branches. Order is hashed
    // nowhere directly, but node Ids are (they index the researched bitmask), so Ids
    // must never be renumbered.
    public enum TechBranch { Trunk = 0, Economic = 1, Religious = 2, Science = 3, Domain = 4, War = 5 }

    // A node's standing for one owner, for the HUD to colour and gate on. Available
    // is exactly CanResearch == true (prereqs/fork/limit met AND affordable);
    // Unaffordable means only the points are short; Locked means a prereq/fork/limit
    // is unmet; Closed means a sibling fork was already taken.
    public enum TechState { Researched, Available, Unaffordable, Locked, Closed }

    // One node in the web. Prereqs are ALL required (AND); RequiresFork additionally
    // demands that some node in that fork group is already taken (the tier gate).
    // ForkGroup > 0 marks mutually-exclusive sidegrades — taking one closes its
    // siblings. IsCapstone nodes are pick-limited (see CapstoneLimit) and each one
    // unlocks its branch's HIGH goal.
    public readonly struct TechNode
    {
        public readonly int Id;
        public readonly string Name;
        public readonly TechBranch Branch;
        public readonly int Tier;          // 0 trunk · 1..3 branch · 4 capstone
        public readonly int BaseCost;      // research points, before cross-branch penalty
        public readonly int ForkGroup;     // 0 none; siblings in a group are exclusive
        public readonly int RequiresFork;  // 0 none; else a fork group that must be satisfied
        public readonly bool IsCapstone;
        public readonly int[] Prereqs;

        public TechNode(int id, string name, TechBranch branch, int tier, int baseCost,
                        int[] prereqs, int forkGroup = 0, int requiresFork = 0, bool capstone = false)
        {
            Id = id; Name = name; Branch = branch; Tier = tier; BaseCost = baseCost;
            Prereqs = prereqs ?? Array.Empty<int>(); ForkGroup = forkGroup;
            RequiresFork = requiresFork; IsCapstone = capstone;
        }
    }

    public static class TechTree
    {
        // Node Ids — stable, never renumber (they are bit positions in the researched
        // mask). Grouped by branch with room to grow between groups.
        // Trunk 0..9
        public const int Roads = 0, Market = 1, Chapel = 2;   // + Scholar's Hut / Farmstead / Muster when their branches land
        // Religious 10..19
        public const int Shrine = 10, Missionaries = 11, Zealotry = 12, Cathedral = 13, GrandTemple = 14;
        // Economic 20..29
        public const int TradePost = 20, Monopoly = 21, Bourse = 22, BankingHouse = 23, GrandExchange = 24;

        // Fork groups.
        const int ForkHolyOrder = 1;    // Missionaries | Zealotry
        const int ForkGuild = 2;        // Monopoly | Bourse

        // The registry, indexed by Id (gaps are null). Kept small and explicit; the
        // spine ships the trunk unlocks + the full Religious branch, with one Economic
        // node so the escalating cross-branch cost has a second branch to bite on.
        static readonly TechNode?[] _byId = BuildRegistry();

        static TechNode?[] BuildRegistry()
        {
            var nodes = new List<TechNode>
            {
                // --- Trunk: everyone can open these; they gate the branches ---------
                new TechNode(Roads,  "Roads",  TechBranch.Trunk, 0, 8,  new int[0]),
                new TechNode(Chapel, "Chapel", TechBranch.Trunk, 0, 10, new[] { Roads }),   // → Religious
                new TechNode(Market, "Market", TechBranch.Trunk, 0, 10, new[] { Roads }),   // → Economic

                // --- Religious branch: Chapel → Shrine → (fork) → Cathedral → ★ ------
                new TechNode(Shrine,       "Shrine",       TechBranch.Religious, 1, 15, new[] { Chapel }),
                new TechNode(Missionaries, "Missionaries", TechBranch.Religious, 2, 22, new[] { Shrine }, forkGroup: ForkHolyOrder),
                new TechNode(Zealotry,     "Zealotry",     TechBranch.Religious, 2, 22, new[] { Shrine }, forkGroup: ForkHolyOrder),
                new TechNode(Cathedral,    "Cathedral",    TechBranch.Religious, 3, 34, new[] { Shrine }, requiresFork: ForkHolyOrder),
                new TechNode(GrandTemple,  "Grand Temple", TechBranch.Religious, 4, 55, new[] { Cathedral }, capstone: true),

                // --- Economic branch: Market → Trade Post → (fork) → Banking → ★ ----
                new TechNode(TradePost,    "Trade Post",    TechBranch.Economic, 1, 15, new[] { Market }),
                new TechNode(Monopoly,     "Monopoly",      TechBranch.Economic, 2, 24, new[] { TradePost }, forkGroup: ForkGuild),
                new TechNode(Bourse,       "Bourse",        TechBranch.Economic, 2, 24, new[] { TradePost }, forkGroup: ForkGuild),
                new TechNode(BankingHouse, "Banking House", TechBranch.Economic, 3, 36, new[] { TradePost }, requiresFork: ForkGuild),
                new TechNode(GrandExchange,"Grand Exchange",TechBranch.Economic, 4, 58, new[] { BankingHouse }, capstone: true),
            };

            int max = 0;
            foreach (var n in nodes) max = Math.Max(max, n.Id);
            var arr = new TechNode?[max + 1];
            foreach (var n in nodes) arr[n.Id] = n;
            return arr;
        }

        public static int Count => _byId.Length;
        public static bool Exists(int id) => id >= 0 && id < _byId.Length && _byId[id].HasValue;
        public static TechNode Node(int id) => _byId[id].Value;

        // Every defined node, in Id order — for iteration (AI, UI, tests).
        public static IEnumerable<TechNode> All()
        {
            for (int i = 0; i < _byId.Length; i++) if (_byId[i].HasValue) yield return _byId[i].Value;
        }

        // The capstone that unlocks a branch's HIGH goal, or -1 if that branch has no
        // capstone yet (so its HIGH is not tech-gated until the branch is ported).
        public static int CapstoneFor(TechBranch branch)
        {
            for (int i = 0; i < _byId.Length; i++)
                if (_byId[i].HasValue && _byId[i].Value.IsCapstone && _byId[i].Value.Branch == branch)
                    return _byId[i].Value.Id;
            return -1;
        }

        // The scored branch a victory path maps to.
        public static TechBranch BranchOf(VictoryPath path) => path switch
        {
            VictoryPath.Economic => TechBranch.Economic,
            VictoryPath.Religious => TechBranch.Religious,
            VictoryPath.Science => TechBranch.Science,
            _ => TechBranch.Domain,
        };
    }
}
