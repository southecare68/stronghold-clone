using System;

namespace Sim
{
    // Exile & Return — you cannot kill the king. When a realm's LAST keep falls it is
    // NOT eliminated (there is no last-keep-standing win anyway — you win by a crown,
    // never by wiping a rival out). Instead the king flees into exile: the fallen
    // territory is razed, the realm is reset to a bare opening — but its RESEARCHED
    // knowledge and any banked MEDIUM survive, so the comeback has teeth — and after a
    // regroup a fresh keep and a starter camp rise at the most isolated corner of the
    // map. A brutal tempo loss, not a death: the attacker keeps their loot and resets
    // a rival's whole game, but the rival plays on.
    //
    // All of it runs in the tick off shared state in owner order, so every machine
    // exiles and reseats identically. The two bookkeeping slots ride the stock array
    // (hashed + snapshotted). Nothing here fires unless a seated realm loses its last
    // keep — which never happens in the units-only parity scenario, so the frozen
    // Checksum is untouched.
    public sealed partial class Simulation
    {
        const int RegroupTicks = 300;        // ~15s in exile before the king refounds
        const int ExileStartPeasants = 5;
        const int ExileStartWood = 60, ExileStartFood = 120;

        int LiveKeepCount(int owner)
        {
            int n = 0;
            foreach (var b in Buildings) if (b.Alive && b.Owner == owner && b.Type == BuildingType.Keep) n++;
            return n;
        }

        void ResolveExile()
        {
            foreach (var kv in _stock)       // owner order — deterministic
            {
                int owner = kv.Key;
                var s = kv.Value;

                if (LiveKeepCount(owner) > 0)          // seated and standing
                {
                    s[EverSeatedIdx] = 1;
                    s[ReseatTickIdx] = 0;
                    continue;
                }
                if (s[EverSeatedIdx] == 0) continue;   // never held a keep — not a realm in play

                // Keepless and once-seated: begin exile, then refound when the regroup ends.
                if (s[ReseatTickIdx] == 0)
                {
                    BeginExile(owner, s);
                    int at = GameClock + RegroupTicks;
                    s[ReseatTickIdx] = at <= 0 ? 1 : at;   // never 0 while exiled
                }
                else if (GameClock >= s[ReseatTickIdx])
                {
                    Reseat(owner, s);
                }
            }
        }

        // The territory falls: raze what is left of it and reset the realm to a bare
        // opening, KEEPING researched tech and banked MEDIUMs so the comeback has
        // teeth. Victory holds and the 80% latches clear — you hold nothing homeless.
        void BeginExile(int owner, int[] s)
        {
            for (int i = Buildings.Count - 1; i >= 0; i--)
                if (Buildings[i].Owner == owner) { TearDownBuilding(Buildings[i]); Buildings.RemoveAt(i); }

            for (int r = 0; r <= (int)ResourceType.Iron; r++) s[r] = 0;   // wood..iron
            s[(int)ResourceType.Wood] = ExileStartWood;
            s[(int)ResourceType.Food] = ExileStartFood;
            s[GoldIdx] = 0;
            s[WeaponsIdx] = 0;
            s[ResearchIdx] = 0;                                  // banked points lost; the tech MASK stays
            s[PopIdx] = 0; s[TaxIdx] = 0; s[RationIdx] = 0;      // 0 so the new keep re-opens the realm
            s[FaithIdx] = 0;                                     // re-seeds to the resting congregation at the new keep
            for (int p = 0; p < PathCount; p++) { s[VicHoldBase + p] = 0; s[VicAnnBase + p] = 0; }
            for (int i = 0; i < SpyCount; i++) s[SpyReadyBase + i] = 0;
            for (int g = 0; g < MarketGoodCount; g++) s[MarketPolicyBase + g] = 0;
            // Tech mask (TechBase..) and VicMedBase are deliberately left intact.

            RaiseAvenger(owner);

            _victoryEvents.Add(new VictoryEvent(VictoryEventKind.Exiled, owner, VictoryPath.Economic));
        }

        // The king's champion rises from the ruins — a single, immense unit spawned
        // right where the last keep fell, in the midst of the attacker who just razed
        // it. This is the whole deterrent: landing the killing blow means facing the
        // Avenger. Only exile ever raises it (design flagged Trainable = false), and if
        // no such design is registered (a bare test sim) it simply does not appear.
        void RaiseAvenger(int owner)
        {
            int design = AvengerDesign();
            if (design < 0) return;
            Tile at = _fallenKeepTile.TryGetValue(owner, out var t) ? t : OwnerAnchor(owner);
            var spot = NearestFreeTile(at.X, at.Y) ?? at;
            SpawnUnit(owner, spot.X, spot.Y, design);
        }

        // The registered special (non-trainable) design — the Avenger. -1 if none.
        int AvengerDesign()
        {
            for (int i = 0; i < DesignList.Count; i++) if (!DesignList[i].Trainable) return i;
            return -1;
        }

        // A fallback spawn spot when no keep was recorded as felled this tick (e.g. a
        // keep lost to annexation, not destruction): the owner's first surviving unit,
        // else the middle of the map.
        Tile OwnerAnchor(int owner)
        {
            foreach (var u in Units) if (u.Alive && u.Owner == owner) return new Tile(Fixed.ToInt(u.X), Fixed.ToInt(u.Y));
            return new Tile(Map.Width / 2, Map.Height / 2);
        }

        // The king refounds: a fresh keep at the most isolated corner, a starter camp
        // around it. The drop-off resets to the new seat — the old one fell with the
        // old keep. If the map has no room right now, wait out another regroup.
        void Reseat(int owner, int[] s)
        {
            var site = FindExileSite(owner);
            var keep = site == null ? null : PlaceBuilding(BuildingType.Keep, owner, site.Value.X, site.Value.Y);
            if (keep == null)
            {
                int at = GameClock + RegroupTicks;
                s[ReseatTickIdx] = at <= 0 ? 1 : at;
                return;
            }
            var drop = SpawnPointAround(keep) ?? new Tile(keep.CenterX, keep.CenterY);
            SetDropOff(owner, drop.X, drop.Y);
            for (int i = 0; i < ExileStartPeasants; i++) SpawnPeasant(owner);
            s[ReseatTickIdx] = 0;

            _victoryEvents.Add(new VictoryEvent(VictoryEventKind.Refounded, owner, VictoryPath.Economic));
        }

        // The buildable tile farthest from every standing keep — the safest empty
        // corner to plant a new seat. Scanned on a coarse grid, deterministically.
        Tile? FindExileSite(int owner)
        {
            Tile best = default; long bestScore = -1; bool found = false;
            for (int y = 3; y < Map.Height - 4; y += 2)
            for (int x = 3; x < Map.Width - 4; x += 2)
            {
                if (!CanPlace(BuildingType.Keep, x, y)) continue;
                long d = MinKeepDistSq(x, y);
                if (d > bestScore) { bestScore = d; best = new Tile(x, y); found = true; }
            }
            return found ? best : (Tile?)null;
        }

        long MinKeepDistSq(int x, int y)
        {
            long best = long.MaxValue;
            foreach (var b in Buildings)
                if (b.Alive && b.Type == BuildingType.Keep)
                {
                    long dx = b.CenterX - x, dy = b.CenterY - y;
                    long d = dx * dx + dy * dy;
                    if (d < best) best = d;
                }
            return best;   // long.MaxValue when no keep stands — anywhere is fine
        }
    }
}
