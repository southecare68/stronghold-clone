namespace Sim
{
    // Veterancy — a unit that survives battle and slays foes hardens. Each enemy UNIT
    // it fells is a kill; at VeteranAt kills it becomes a Veteran, at EliteAt an Elite,
    // each rank adding to its hit points and the force of its blows. A promotion
    // toughens it on the spot — its max hp grows and the gain heals it — so keeping a
    // unit alive through a hard fight leaves it stronger AND patched up.
    //
    // All integer and deterministic; the kill count rides the hash & snapshot, never
    // the frozen units-only Checksum (the parity scenario is Move-only, so no unit ever
    // kills, and a rank-0 unit's damage is byte-identical to before veterancy existed).
    public sealed partial class Simulation
    {
        const int VeteranAt = 2, EliteAt = 5;                 // kills to reach each rank
        static readonly int[] RankBonusPct = { 0, 25, 50 };   // +% to hp & damage, by rank (Regular / Veteran / Elite)

        // A unit's rank from its kill count: 0 Regular, 1 Veteran, 2 Elite. Public for
        // the HUD (a chevron over the veterans, the rank in the selection readout).
        public int RankOf(Unit u) => u.Kills >= EliteAt ? 2 : u.Kills >= VeteranAt ? 1 : 0;

        static int VetScale(int baseValue, int rank) => baseValue * (100 + RankBonusPct[rank]) / 100;

        // The force of this unit's blow, veterancy folded in — used for every strike.
        // A Regular (rank 0) scales by ×1, so its damage is exactly what it always was.
        int VetDamage(Unit u, int baseDamage) => VetScale(baseDamage, RankOf(u));

        // Record a kill and promote if it crosses a rank threshold: raise MaxHp to the
        // new rank's value and heal the difference. Called wherever a unit's blow fells
        // an enemy UNIT (field combat and rampart fire).
        void RegisterKill(Unit killer)
        {
            killer.Kills++;
            int newMax = VetScale(DesignOf(killer.DesignId).Hp, RankOf(killer));
            if (newMax > killer.MaxHp) { killer.Hp += newMax - killer.MaxHp; killer.MaxHp = newMax; }
            AwardGlory(killer);   // a felled foe adds renown to the slayer's court (Prestige.cs)
        }
    }
}
