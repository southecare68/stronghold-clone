// Guard — a defensive stance. Troops set to guard hold a post and automatically
// intercept any enemy that enters their realm's territory, falling back once the
// ground is clear so they defend the line rather than chase off across the map. The
// same pass warns any realm — guarded or not — the moment a foe sets foot on its
// land. Deterministic: id-order iteration, the SAME HomeRect the border is drawn
// around, and the seeded fog deciding who can be seen.

namespace Sim
{
    public partial class Simulation
    {
        const int GuardEvery = 10;              // re-evaluate guards / raise alerts every N ticks
        const int AlertInterval = 8 * TickRate; // gap between "enemy in your lands" alerts, per realm

        void ResolveGuard()
        {
            if (TickNumber == 0 || TickNumber % GuardEvery != 0) return;

            // Realms to consider: every keep-holder (so even a guardless realm is warned)
            // and anyone with a guard posted.
            var realms = new System.Collections.Generic.SortedSet<int>();
            foreach (var b in Buildings) if (b.Alive && b.Type == BuildingType.Keep) realms.Add(b.Owner);
            foreach (var u in Units) if (u.Guarding && u.Alive) realms.Add(u.Owner);

            foreach (int owner in realms)       // owner order
            {
                var rect = HomeRect(owner);
                if (rect == null) continue;

                // Enemy units on this realm's land that it can actually see (a stealth
                // scout slips past a guard), gathered in id order.
                var intruders = new System.Collections.Generic.List<Unit>();
                foreach (var e in Units)
                {
                    if (!e.Alive || e.Owner == owner) continue;
                    if (!InRect(rect, Fixed.ToInt(e.X), Fixed.ToInt(e.Y))) continue;
                    if (!CanSeeUnit(owner, e)) continue;
                    intruders.Add(e);
                }

                // The alert — a foe on your land — throttled per realm.
                var s = StockOf(owner);
                if (s[AlertCdIdx] > 0) s[AlertCdIdx] = System.Math.Max(0, s[AlertCdIdx] - GuardEvery);
                if (intruders.Count > 0 && s[AlertCdIdx] == 0)
                {
                    var n = intruders[0];
                    _scoutSightings.Add(new ScoutSighting(owner, n.Owner, Fixed.ToInt(n.X), Fixed.ToInt(n.Y), SightingKind.Intruder));
                    s[AlertCdIdx] = AlertInterval;
                }

                // Direct the guards.
                foreach (var g in Units)
                {
                    if (!g.Guarding || !g.Alive || g.Owner != owner) continue;
                    if (intruders.Count > 0)
                    {
                        // Keep chasing a current target that's still a live intruder here;
                        // otherwise lock onto the nearest one.
                        bool keep = g.TargetId != 0 && intruders.Exists(x => x.Id == g.TargetId);
                        if (!keep)
                        {
                            g.Job = Job.None;
                            g.TargetBuildingId = 0;
                            g.TargetId = NearestOf(intruders, Fixed.ToInt(g.X), Fixed.ToInt(g.Y)).Id;
                        }
                    }
                    else
                    {
                        // No threat: drop the chase and fall back to the post.
                        g.TargetId = 0;
                        int gx = Fixed.ToInt(g.X), gy = Fixed.ToInt(g.Y);
                        if ((gx != g.GuardX || gy != g.GuardY) && !g.HasPath)
                            Order(g, g.GuardX, g.GuardY);
                    }
                }
            }
        }

        static Unit NearestOf(System.Collections.Generic.List<Unit> us, int x, int y)
        {
            Unit best = null; long bd = long.MaxValue;
            foreach (var u in us)   // id order → deterministic tiebreak
            {
                long dx = Fixed.ToInt(u.X) - x, dy = Fixed.ToInt(u.Y) - y;
                long d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = u; }
            }
            return best;
        }
    }
}
