// Vision.cs — fog of war, per player, as a real game rule.
//
// Two different things get called "fog of war", and the difference matters here:
//
//   Explored — every tile this player has EVER seen. Accumulates and never
//              clears. This is genuine accumulated game state: it depends on the
//              whole history of the match, two machines could disagree about it,
//              and it gates orders (you cannot send a worker to a patch you have
//              never found). So it is checksummed and it travels in a snapshot.
//
//   Visible  — the tiles this player can see RIGHT NOW. This is a pure function
//              of where that player's units and buildings currently stand, so
//              two machines that agree on unit positions cannot disagree about
//              it. It is recomputed each tick and is deliberately NOT hashed and
//              NOT snapshotted: hashing derived state adds no detection power
//              and only invites the two to fall out of step over an ordering
//              detail. Restoring a snapshot recomputes it from the units.
//
// Everything here is integer-only and iterates in fixed order, like the rest of
// the sim. Sight is blocked by rock (see TileMap.HasSightLine), which is what
// makes the skirmish map's ridge more than decoration — you genuinely cannot see
// what is massing on the other side of it.

using System;
using System.Collections.Generic;

namespace Sim
{
    public sealed class Vision
    {
        // How far things see, in tiles. Sight is NOT part of the point-buy budget
        // on purpose: making it purchasable would mean re-costing every existing
        // design, and a roster balanced around 43 points is one of the more
        // heavily verified things in the project. Every unit sees the same
        // distance; a keep sees further because it is tall.
        public const int UnitSight = 7;
        public static int SightOf(BuildingType t) => t switch
        {
            BuildingType.Keep => 10,
            BuildingType.Barracks => 7,
            BuildingType.Gatehouse => 5,
            _ => 4,               // wall
        };

        readonly TileMap _map;
        readonly int _words;      // 32-bit words needed to cover the whole map

        // Sorted so iteration order is owner order on every machine — the same
        // reason the stockpiles are a SortedDictionary.
        readonly SortedDictionary<int, uint[]> _explored = new();
        readonly SortedDictionary<int, uint[]> _visible = new();

        public Vision(TileMap map)
        {
            _map = map;
            _words = (map.Width * map.Height + 31) / 32;
        }

        public int Words => _words;
        public IReadOnlyDictionary<int, uint[]> Explored => _explored;

        // Owners appear here the moment they own anything, so a player with no
        // units and no buildings simply has no entry rather than an empty array
        // that would still have to be hashed.
        uint[] BitsFor(SortedDictionary<int, uint[]> d, int owner)
        {
            if (!d.TryGetValue(owner, out var bits)) d[owner] = bits = new uint[_words];
            return bits;
        }

        static bool Get(uint[] bits, int i) => (bits[i >> 5] & (1u << (i & 31))) != 0;
        static void Set(uint[] bits, int i) => bits[i >> 5] |= 1u << (i & 31);

        public bool IsVisible(int owner, int x, int y) =>
            _map.InBounds(x, y) && _visible.TryGetValue(owner, out var b) && Get(b, _map.Index(x, y));

        public bool IsExplored(int owner, int x, int y) =>
            _map.InBounds(x, y) && _explored.TryGetValue(owner, out var b) && Get(b, _map.Index(x, y));

        // A fixed-point position, which is what units carry.
        public bool IsVisibleAt(int owner, int fx, int fy) =>
            IsVisible(owner, Fixed.ToInt(fx), Fixed.ToInt(fy));

        // Rebuild Visible from scratch and fold it into Explored. Called at the
        // start of every Tick, so a command is judged against the world as it
        // stood when the player could last have seen it — and, crucially, at the
        // same point in the sequence on every machine.
        public void Update(IReadOnlyList<Unit> units, IReadOnlyList<Building> buildings, Func<Unit, int> sightOf = null) =>
            Recompute(units, buildings, accumulate: true, sightOf);

        // Rebuild Visible WITHOUT adding anything to Explored. This is what a
        // rejoiner needs, and the distinction is not cosmetic: exploration is
        // folded in at the TOP of a tick, so by the time a snapshot is taken the
        // units have since moved. Accumulating from those end-of-tick positions
        // would leave the rejoiner knowing a sliver more of the map than the
        // player who sent it — a real desync, and one that would only show up
        // later as an order one machine allowed and the other refused.
        public void RecomputeVisible(IReadOnlyList<Unit> units, IReadOnlyList<Building> buildings, Func<Unit, int> sightOf = null) =>
            Recompute(units, buildings, accumulate: false, sightOf);

        void Recompute(IReadOnlyList<Unit> units, IReadOnlyList<Building> buildings, bool accumulate, Func<Unit, int> sightOf)
        {
            foreach (var kv in _visible) Array.Clear(kv.Value, 0, kv.Value.Length);

            foreach (var u in units)                 // id order
            {
                if (!u.Alive) continue;
                // Per-unit sight (a scout sees far); default to the classic radius
                // when no resolver is supplied, so every existing caller is unchanged.
                int r = sightOf != null ? sightOf(u) : UnitSight;
                Reveal(u.Owner, Fixed.ToInt(u.X), Fixed.ToInt(u.Y), r, accumulate);
            }

            foreach (var b in buildings)             // id order
            {
                if (!b.Alive) continue;
                Reveal(b.Owner, b.CenterX, b.CenterY, SightOf(b.Type), accumulate);
            }
        }

        // Light a disc around one watcher, minus whatever the terrain hides.
        void Reveal(int owner, int cx, int cy, int radius, bool accumulate)
        {
            var vis = BitsFor(_visible, owner);
            // Only touch Explored when accumulating. BitsFor would otherwise CREATE
            // a zero-filled owner entry even on a non-accumulating recompute, and
            // StateChecksum hashes the entry count — so a rejoiner that recomputes
            // visibility over a not-yet-explored start would carry an empty entry
            // the host lacks, and flag a phantom desync at tick 0.
            var seen = accumulate ? BitsFor(_explored, owner) : null;
            int r2 = radius * radius;

            int x0 = Math.Max(0, cx - radius), x1 = Math.Min(_map.Width - 1, cx + radius);
            int y0 = Math.Max(0, cy - radius), y1 = Math.Min(_map.Height - 1, cy + radius);

            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy > r2) continue;

                    int i = _map.Index(x, y);
                    // Already lit by another watcher this tick — the expensive
                    // trace would only reach the same answer.
                    if (Get(vis, i)) continue;
                    if (!_map.HasSightLine(cx, cy, x, y)) continue;

                    Set(vis, i);
                    if (accumulate) Set(seen, i);
                }

            // Second pass: fill the single-tile gaps a per-tile raycast leaves at
            // the frontier. A naive "is the centre of this tile in line of sight"
            // test speckles the rim of the disc — some tiles pass, their
            // neighbours don't — which looked like dithered fog and, worse, made a
            // dragged wall skip tiles it could not be built on. Here a tile is
            // marked EXPLORED (remembered) when the neighbour one step back toward
            // the watcher is visible, so the fill follows the light outward and
            // never leaks behind rock — a shadowed tile has no lit inner neighbour.
            // Only Explored is touched; Visible stays exactly what strict
            // line-of-sight found, so vision, target acquisition and the sim
            // checksum are unchanged — this smooths only what the player remembers.
            if (accumulate)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int dx = x - cx, dy = y - cy;
                        if (dx * dx + dy * dy > r2) continue;
                        int i = _map.Index(x, y);
                        if (Get(seen, i)) continue;
                        int nx = x - Math.Sign(dx), ny = y - Math.Sign(dy);
                        if (Get(vis, _map.Index(nx, ny))) Set(seen, i);
                    }
        }

        // ---- snapshot / restore -------------------------------------------
        // Only Explored travels. Visible is derived, so a rejoiner recomputes it
        // from the units it was handed rather than trusting a copy of it.

        public Dictionary<int, uint[]> CopyExplored()
        {
            var copy = new Dictionary<int, uint[]>();
            foreach (var kv in _explored) copy[kv.Key] = (uint[])kv.Value.Clone();
            return copy;
        }

        public void RestoreExplored(IReadOnlyDictionary<int, uint[]> explored)
        {
            _explored.Clear();
            _visible.Clear();
            if (explored == null) return;
            foreach (var kv in explored)
            {
                // A snapshot from a machine that disagreed about the map size is
                // not something to paper over — take what fits and let the
                // checksum comparison report it.
                var bits = new uint[_words];
                int n = Math.Min(_words, kv.Value.Length);
                Array.Copy(kv.Value, bits, n);
                _explored[kv.Key] = bits;
            }
        }

        // Mixed into StateChecksum. Explored is the half that can genuinely
        // diverge, so this is the half that gets hashed.
        public void MixInto(Action<int> mix)
        {
            mix(_explored.Count);
            foreach (var kv in _explored)           // owner order
            {
                mix(kv.Key);
                foreach (uint w in kv.Value) mix(unchecked((int)w));
            }
        }
    }
}
