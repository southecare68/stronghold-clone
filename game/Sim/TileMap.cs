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

        public TileMap(int width, int height, Terrain fill = Terrain.Ground)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"map must have positive size, got {width}x{height}");

            Width = width;
            Height = height;
            _tiles = new Terrain[width * height];
            if (fill != Terrain.Ground)
                for (int i = 0; i < _tiles.Length; i++) _tiles[i] = fill;
        }

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

        public int Index(int x, int y) => y * Width + x;

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public Terrain At(int x, int y) => _tiles[Index(x, y)];

        public void Set(int x, int y, Terrain t) => _tiles[Index(x, y)] = t;

        public bool Passable(int x, int y) =>
            InBounds(x, y) && At(x, y) != Terrain.Water && At(x, y) != Terrain.Rock;

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

                if (groundOnly) { if (At(x, y) != Terrain.Ground) return false; }
                else if (!Passable(x, y)) return false;
            }
            return true;
        }

        // Build a map from text, which makes test cases readable and lets us
        // hand-author small maps before there is any editor:
        //   '.' ground   '~' water   '#' rock   ',' marsh
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
