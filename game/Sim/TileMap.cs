// TileMap.cs — The ground everything else in Phase 2 stands on.
//
// Buildings occupy tiles, resource nodes sit on tiles, pathfinding walks tiles,
// and combat happens between things positioned on tiles. So the map comes first.
//
// WHY THIS IS NOT IN THE CHECKSUM. Terrain here is immutable: it is built once,
// identically on every machine, and never changes during a match. State that
// cannot diverge does not need hashing, and hashing it every tick for thousands
// of tiles would cost real time for no information. **If terrain ever becomes
// destructible** — a breached wall lowering to rubble, a mined-out rock — that
// stops being true and the mutable part must go into Simulation.Checksum() and
// into MatchSnapshot on the same day.
//
// Costs are integers, in tenths, so a diagonal step can be 14 against an
// orthogonal 10 — an integer stand-in for sqrt(2) that keeps the whole
// pathfinder free of floating point.

using System;

namespace Sim
{
    public enum Terrain : byte
    {
        Ground = 0,
        Water = 1,      // impassable
        Rock = 2,       // impassable; later, the thing quarries are built on
        Marsh = 3,      // passable but slow
        // Fertile soil, in three grades — the only ground a farm's field yields on,
        // and the richer the grade the more grain each reap brings (see FieldYield).
        // Fertile stays the middle grade so older tests that set Terrain.Fertile keep
        // getting an ordinary, working field.
        Fertile = 4,        // normal soil
        FertilePoor = 5,    // thin soil, a lean field
        FertileRich = 6,    // prime soil, a bumper field
    }

    // A whole-tile coordinate. Distinct from a Unit's fixed-point position on
    // purpose: units stand at sub-tile precision, but tiles are counted, and
    // mixing the two up is how a unit ends up standing 1/65536 of a tile inside
    // a wall.
    public readonly struct Tile : IEquatable<Tile>
    {
        public readonly int X;
        public readonly int Y;

        public Tile(int x, int y) { X = x; Y = y; }

        public bool Equals(Tile other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Tile t && Equals(t);
        public override int GetHashCode() => (X * 397) ^ Y;
        public override string ToString() => $"({X},{Y})";
    }

    public sealed class TileMap
    {
        public const int StepCost = 10;          // one orthogonal step on clear ground
        public const int DiagonalCost = 14;      // ~10 * sqrt(2), in the same tenths
        public const int MarshCost = 25;         // crossable, but you would rather not

        public readonly int Width;
        public readonly int Height;

        readonly Terrain[] _tiles;

        // Building occupancy, laid over the terrain. This is MUTABLE and is
        // deliberately NOT part of the fingerprint: the fingerprint proves two
        // machines loaded the same terrain, while occupancy is derived from the
        // buildings list (which IS in StateChecksum), so hashing it here would be
        // redundant — and it changes during a match, which terrain must never do.
        readonly bool[] _blocked;

        public TileMap(int width, int height, Terrain fill = Terrain.Ground)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"map must have positive size, got {width}x{height}");

            Width = width;
            Height = height;
            _tiles = new Terrain[width * height];
            _blocked = new bool[width * height];
            if (fill != Terrain.Ground)
                for (int i = 0; i < _tiles.Length; i++) _tiles[i] = fill;
        }

        // ---- Building occupancy ----------------------------------------------
        public bool Blocked(int x, int y) => _blocked[Index(x, y)];
        public void SetBlocked(int x, int y, bool v) => _blocked[Index(x, y)] = v;
        public void ClearBlocked() => Array.Clear(_blocked, 0, _blocked.Length);

        // A hash of the whole map, computed once. Terrain itself is not
        // checksummed per tick (it never changes), but the two machines must be
        // playing the SAME map — and nothing else would notice if they weren't.
        // Mixing this one number into Simulation.StateChecksum turns "we
        // silently loaded different maps" from an unexplained desync fifty ticks
        // in, into a mismatch on the very first comparison.
        public uint Fingerprint { get; private set; }

        public void SealTerrain()
        {
            uint h = 0x811c9dc5;
            void Mix(int n)
            {
                for (int i = 0; i < 4; i++)
                {
                    h ^= (uint)((n >> (i * 8)) & 0xff);
                    h *= 0x01000193;
                }
            }
            Mix(Width);
            Mix(Height);
            foreach (var t in _tiles) Mix((int)t);
            Fingerprint = h;
        }

        // Copy the terrain out, and rebuild a map from such a copy. Used by the
        // replay system to record and reconstruct the exact battlefield — a
        // replay of a match on a different map would desync at tick 0.
        public Terrain[] CopyTiles() => (Terrain[])_tiles.Clone();

        public static TileMap FromTiles(int width, int height, Terrain[] tiles)
        {
            var map = new TileMap(width, height);
            if (tiles != null && tiles.Length == map._tiles.Length)
                Array.Copy(tiles, map._tiles, tiles.Length);   // _tiles contents are mutable; the field is readonly
            map.SealTerrain();
            return map;
        }

        public int Index(int x, int y) => y * Width + x;

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public Terrain At(int x, int y) => _tiles[Index(x, y)];

        public void Set(int x, int y, Terrain t) => _tiles[Index(x, y)] = t;

        public bool Passable(int x, int y) =>
            InBounds(x, y) && At(x, y) != Terrain.Water && At(x, y) != Terrain.Rock
            && !_blocked[Index(x, y)];

        // Soil a farm's field will grow on (any grade). Ordinary to walk — it only
        // matters to the farm, which sows its wheat here and nowhere else.
        public bool IsFertile(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            var t = _tiles[Index(x, y)];
            return t == Terrain.Fertile || t == Terrain.FertilePoor || t == Terrain.FertileRich;
        }

        // Grain a field reaps per gather on this tile: richer soil, more per reap.
        // Zero off fertile ground — nothing grows there. This is the "resource value"
        // a tile carries, shown while placing a farm.
        public int FieldYield(int x, int y) => !InBounds(x, y) ? 0 : _tiles[Index(x, y)] switch
        {
            Terrain.FertilePoor => 1,
            Terrain.Fertile => 2,
            Terrain.FertileRich => 3,
            _ => 0,
        };

        // Cost of ENTERING this tile, before the diagonal surcharge.
        public int EnterCost(int x, int y) =>
            At(x, y) == Terrain.Marsh ? MarshCost : StepCost;

        // Is there an unobstructed straight line from one tile to another? Used
        // for line of sight (later: vision, ranged fire) and as the geometric
        // half of path smoothing.
        //
        // Integer Bresenham, applying the SAME strict corner rule as the
        // pathfinder — a diagonal needs both flanking tiles clear. If the two
        // disagreed, a straightened route could clip a wall corner the
        // pathfinder had deliberately routed around.
        public bool HasLineOfSight(int x0, int y0, int x1, int y1) =>
            TraceLine(x0, y0, x1, y1, groundOnly: false);

        // A straight run that a smoother may collapse onto: unobstructed AND
        // crossing nothing costlier than plain ground.
        //
        // This is the fix for the obvious trap: line of sight alone ignores
        // terrain COST. Marsh is passable, so plain LOS would happily straighten
        // a detour A* computed to AVOID the marsh right back through it — the
        // shortcut is shorter in tiles but more expensive to walk. Restricting
        // shortcuts to ground keeps cost-optimal detours intact, while uniform
        // open ground still collapses to a single leg (which is what keeps
        // straight-line movement, and 0xB1A7A676, unchanged).
        public bool HasClearRun(int x0, int y0, int x1, int y1) =>
            TraceLine(x0, y0, x1, y1, groundOnly: true);

        // Does this tile stop you SEEING past it? Deliberately not the same
        // question as Passable. A lake is impassable but you can see clean across
        // it; rock is what actually hides an army. Buildings are excluded too —
        // making walls opaque sounds right until your own castle blinds you, and
        // it would mean a player could darken their opponent's view by building.
        //
        // Off-map counts as blocking so a trace that wanders out cannot read past
        // the edge.
        public bool BlocksSight(int x, int y) =>
            !InBounds(x, y) || _tiles[Index(x, y)] == Terrain.Rock;

        // Can a watcher at one tile see another? Same integer Bresenham as the
        // other traces, so it is exactly reproducible on every machine — vision
        // gates orders, which makes it game state, which makes "close enough"
        // a desync.
        //
        // Only the tiles BETWEEN the two are tested: standing on rock you can
        // still see out, and a rock face is itself visible from in front of it.
        // No corner rule here — sight squeezing diagonally past a corner is
        // realistic, where a unit's body squeezing through one is not.
        public bool HasSightLine(int x0, int y0, int x1, int y1)
        {
            if (!InBounds(x0, y0) || !InBounds(x1, y1)) return false;

            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int x = x0, y = y0;

            while (true)
            {
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x += sx; }
                if (e2 < dx) { err += dx; y += sy; }
                if (x == x1 && y == y1) return true;
                if (BlocksSight(x, y)) return false;
            }
        }

        bool TraceLine(int x0, int y0, int x1, int y1, bool groundOnly)
        {
            if (!Passable(x0, y0) || !Passable(x1, y1)) return false;

            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int x = x0, y = y0;

            while (x != x1 || y != y1)
            {
                int e2 = 2 * err;
                bool stepX = e2 > -dy;
                bool stepY = e2 < dx;

                if (stepX && stepY)
                {
                    // Clip prevention always keys on passability, never cost: a
                    // diagonal does not enter the flanking tiles, so their cost
                    // is irrelevant — only whether they would let the line
                    // squeeze through a shut corner.
                    if (!Passable(x + sx, y) || !Passable(x, y + sy)) return false;
                    err += dx - dy;
                    x += sx;
                    y += sy;
                }
                else if (stepX) { err -= dy; x += sx; }
                else { err += dx; y += sy; }

                if (groundOnly) { if (At(x, y) != Terrain.Ground && !IsFertile(x, y)) return false; }
                else if (!Passable(x, y)) return false;
            }
            return true;
        }

        // Build a map from text, which makes test cases readable and lets us
        // hand-author small maps before there is any editor:
        //   '.' ground   '~' water   '#' rock   ',' marsh
        //   fertile soil by grade:   '-' poor   '=' normal   '+' rich
        public static TileMap FromRows(params string[] rows)
        {
            if (rows == null || rows.Length == 0)
                throw new ArgumentException("a map needs at least one row", nameof(rows));

            int w = rows[0].Length;
            foreach (var r in rows)
                if (r.Length != w)
                    throw new ArgumentException("all map rows must be the same length", nameof(rows));

            var map = new TileMap(w, rows.Length);
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < w; x++)
                    map.Set(x, y, rows[y][x] switch
                    {
                        '~' => Terrain.Water,
                        '#' => Terrain.Rock,
                        ',' => Terrain.Marsh,
                        '-' => Terrain.FertilePoor,
                        '=' => Terrain.Fertile,
                        '+' => Terrain.FertileRich,
                        _ => Terrain.Ground,
                    });
            map.SealTerrain();
            return map;
        }

        // The default world: empty ground, big enough for anything the vertical
        // slice does. An empty map matters more than it looks — with nothing to
        // route around, every path smooths to a single straight leg, which is
        // exactly the movement the simulation had before pathfinding existed.
        public const int DefaultSize = 128;

        public static TileMap Open(int size = DefaultSize)
        {
            var map = new TileMap(size, size);
            map.SealTerrain();
            return map;
        }

        // A hand-authored map with something to walk around, for seeing path
        // following actually work. Both machines must build the identical map —
        // which the fingerprint in StateChecksum now enforces.
        public static TileMap Demo(int size = DefaultSize)
        {
            var map = new TileMap(size, size);

            // A long wall with a single gate, straddling the route between the
            // two starting armies.
            for (int y = 4; y < 34; y++)
                if (y < 18 || y > 21) map.Set(24, y, Terrain.Rock);

            // A lake to force a longer detour further out.
            for (int y = 26; y < 34; y++)
                for (int x = 34; x < 46; x++)
                    map.Set(x, y, Terrain.Water);

            // Boggy ground: passable, but a pathfinder that ignores cost will
            // plough straight through it and look stupid doing so.
            for (int y = 12; y < 20; y++)
                for (int x = 30; x < 38; x++)
                    map.Set(x, y, Terrain.Marsh);

            map.SealTerrain();
            return map;
        }

        // A proper 1v1 skirmish map, hand-authored (no RNG, so every machine
        // builds it identically) and laid out in fractions of `size` so it scales.
        //
        // The shape is deliberate: the two bases sit far apart on the west and
        // east edges, and a north-south mountain ridge divides the map with three
        // passes through it. That turns movement into decisions — which pass to
        // take, which to wall off — instead of a straight line across open ground.
        // Marsh aprons at the middle pass make the most obvious route the slowest,
        // and two lakes off the centre line bend the northern and southern routes
        // without sealing anything.
        //
        // tests/Pathfinding checks the two base areas are actually connected: an
        // authored map that accidentally walls a player in would be a disaster,
        // and it is exactly the sort of thing you only notice mid-match.
        public static TileMap Skirmish(int size = 128)
        {
            var map = new TileMap(size, size);
            int mid = size / 2;

            // The dividing ridge, three tiles thick, with three gaps.
            int top = size * 6 / 100, bottom = size * 94 / 100;
            int[] passes = { size * 25 / 100, mid, size * 75 / 100 };
            const int passHalf = 3;
            for (int y = top; y < bottom; y++)
            {
                bool inPass = false;
                foreach (int p in passes) if (y >= p - passHalf && y <= p + passHalf) inPass = true;
                if (inPass) continue;
                for (int x = mid - 1; x <= mid + 1; x++) map.Fill(x, y, x, y, Terrain.Rock);
            }

            // Lakes, off the centre line so they shape routes rather than block them.
            map.Fill(size * 30 / 100, size * 14 / 100, size * 42 / 100, size * 28 / 100, Terrain.Water);
            map.Fill(size * 58 / 100, size * 72 / 100, size * 70 / 100, size * 86 / 100, Terrain.Water);

            // Boggy ground either side of the middle pass — the shortest way is
            // also the slowest, so the flanking passes are worth considering.
            map.Fill(mid - 8, mid - 6, mid - 3, mid + 6, Terrain.Marsh);
            map.Fill(mid + 3, mid - 6, mid + 8, mid + 6, Terrain.Marsh);

            // Outcrops for texture and a little cover near each base approach.
            map.Fill(size * 18 / 100, size * 60 / 100, size * 24 / 100, size * 64 / 100, Terrain.Rock);
            map.Fill(size * 76 / 100, size * 36 / 100, size * 82 / 100, size * 40 / 100, Terrain.Rock);

            // Fertile soil — the only ground a farm's field grows on. One patch by
            // each keep, on its drop-off side so the harvest hauls home on a clear
            // path. Each patch is GRADED across its rows — thin soil, then ordinary,
            // then prime — so even within a patch WHERE you sow decides how much you
            // reap. Limited and clear of the home wood, stone and iron.
            map.FillFertile(size * 8 / 100,  size * 46 / 100, size * 13 / 100, size * 56 / 100);
            map.FillFertile(size * 87 / 100, size * 46 / 100, size * 92 / 100, size * 56 / 100);

            map.SealTerrain();
            return map;
        }

        // Paint a rectangle of terrain, clamped to the map.
        void Fill(int x0, int y0, int x1, int y1, Terrain t)
        {
            for (int y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++)
                    _tiles[Index(x, y)] = t;
        }

        // Paint a fertile patch graded across its rows: thin soil along the top, then
        // ordinary, then prime along the bottom (the drop-off/keep side). Three plain
        // bands rather than a scatter, so a player can read the good rows at a glance
        // and aim a field at them — and so both mirrored patches grade identically.
        void FillFertile(int x0, int y0, int x1, int y1)
        {
            int lo = Math.Max(0, y0), hi = Math.Min(Height - 1, y1);
            int span = Math.Max(1, hi - lo + 1);
            for (int y = lo; y <= hi; y++)
            {
                int band = (y - lo) * 3 / span;   // 0 (top), 1, 2 (bottom)
                Terrain t = band == 0 ? Terrain.FertilePoor : band == 1 ? Terrain.Fertile : Terrain.FertileRich;
                for (int x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++)
                    _tiles[Index(x, y)] = t;
            }
        }

        // A deterministic scatter of obstacles: same seed, same map, on every
        // machine and every run. Not a real map generator — a stand-in so the
        // pathfinder can be exercised against something less tidy than a room —
        // but deterministic from the start, because a generator that is only
        // *nearly* reproducible desyncs a match at tick 0.
        public static TileMap Generate(int width, int height, uint seed)
        {
            var map = new TileMap(width, height);
            var rng = new Rng(seed);

            int blobs = (width * height) / 60;
            for (int i = 0; i < blobs; i++)
            {
                int cx = rng.NextInt(width);
                int cy = rng.NextInt(height);
                int r = rng.NextInt(1, 3);
                var kind = rng.NextInt(3) == 0 ? Terrain.Water : Terrain.Rock;

                for (int y = cy - r; y <= cy + r; y++)
                    for (int x = cx - r; x <= cx + r; x++)
                    {
                        if (!map.InBounds(x, y)) continue;
                        // Round-ish blobs: skip the corners of the square.
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r) continue;
                        map.Set(x, y, kind);
                    }
            }

            int marshes = (width * height) / 120;
            for (int i = 0; i < marshes; i++)
            {
                int cx = rng.NextInt(width);
                int cy = rng.NextInt(height);
                if (map.At(cx, cy) == Terrain.Ground) map.Set(cx, cy, Terrain.Marsh);
            }

            map.SealTerrain();
            return map;
        }
    }
}
