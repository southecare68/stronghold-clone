// PathFinder.cs — Deterministic grid A* over a TileMap.
//
// THE ORDERING TRAP, AGAIN. On an open map, dozens of routes tie for shortest.
// Which one A* returns is decided entirely by how the open set breaks ties — and
// if that tie-break depends on insertion order, or on iterating a hash set, two
// machines pick different routes, units walk different ways, and the match
// desyncs. It is exactly the bug tests/CommandOrder exists for, in a new place.
//
// So the open set is ordered by (F, H, tile index): a TOTAL order in which no
// two distinct tiles ever compare equal, fixed by the map's geometry rather than
// by the order anything happened to be discovered. Neighbours are visited in a
// constant direction order for the same reason.
//
// Everything is integers. No floats, no Math.Sqrt: the heuristic is octile
// distance in the same tenths as the step costs, which is admissible (never
// overestimates) and therefore still yields shortest paths.
//
// Allocation: the working arrays are allocated once per PathFinder and reused,
// and "cleared" between searches by a generation stamp rather than by wiping
// them. An RTS asks for a lot of paths; clearing a 100k-tile array per request
// would dominate the cost of finding the path.

using System;
using System.Collections.Generic;

namespace Sim
{
    public sealed class PathFinder
    {
        // A search that has expanded this many tiles is almost certainly walking
        // an enclosed region or being asked for something impossible. Give up
        // rather than spend an unbounded slice of a tick on it: a client that
        // takes 400 ms on one tick has stalled every other player in the match.
        public const int DefaultMaxExpansions = 20000;

        // Constant visit order. Orthogonals first, then diagonals — with the
        // (F, H, index) tie-break below, this ordering is not what makes the
        // result deterministic, but keeping it fixed means a change here can
        // never quietly become a change in behaviour.
        static readonly int[] Dx = { 0, 1, 0, -1, 1, 1, -1, -1 };
        static readonly int[] Dy = { -1, 0, 1, 0, -1, 1, 1, -1 };

        readonly TileMap _map;
        readonly int[] _g;
        readonly int[] _cameFrom;
        readonly int[] _seenStamp;
        readonly int[] _closedStamp;
        int _stamp;

        // The open set. Keys are stored IN the heap rather than read from _g at
        // comparison time: when a tile's cost improves we push a fresh entry, and
        // if the old entry's key could change underneath it the heap invariant
        // would break and the cheapest tile would not be at the root.
        int[] _heapNode;
        int[] _heapF;
        int[] _heapH;
        int _heapCount;

        public int MaxExpansions { get; set; } = DefaultMaxExpansions;

        // Tiles expanded by the last search. Useful for tuning MaxExpansions and
        // for spotting a query that is quietly costing far more than it should.
        public int LastExpansions { get; private set; }

        public PathFinder(TileMap map)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            int n = map.Width * map.Height;

            _g = new int[n];
            _cameFrom = new int[n];
            _seenStamp = new int[n];
            _closedStamp = new int[n];

            int capacity = Math.Max(16, n / 4);
            _heapNode = new int[capacity];
            _heapF = new int[capacity];
            _heapH = new int[capacity];
        }

        // Fills `path` with the tiles to walk, EXCLUDING the starting tile and
        // ending on the goal. Returns false and leaves `path` empty if no route
        // exists, if the goal is unreachable, or if the search hit MaxExpansions.
        //
        // Start == goal is success with an empty path: already there.
        public bool TryFindPath(int startX, int startY, int goalX, int goalY, List<Tile> path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            path.Clear();
            LastExpansions = 0;

            if (!_map.Passable(startX, startY) || !_map.Passable(goalX, goalY)) return false;
            if (startX == goalX && startY == goalY) return true;

            _stamp++;
            _heapCount = 0;

            int start = _map.Index(startX, startY);
            int goal = _map.Index(goalX, goalY);

            _g[start] = 0;
            _cameFrom[start] = -1;
            _seenStamp[start] = _stamp;
            Push(start, Heuristic(startX, startY, goalX, goalY), Heuristic(startX, startY, goalX, goalY));

            while (_heapCount > 0)
            {
                int current = Pop();

                // Lazy deletion: a tile can sit in the heap more than once, and
                // every copy after the first is stale by definition.
                if (_closedStamp[current] == _stamp) continue;
                _closedStamp[current] = _stamp;

                if (current == goal) { Reconstruct(start, goal, path); return true; }

                if (++LastExpansions > MaxExpansions) { path.Clear(); return false; }

                int cx = current % _map.Width;
                int cy = current / _map.Width;

                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + Dx[d];
                    int ny = cy + Dy[d];
                    if (!_map.Passable(nx, ny)) continue;

                    bool diagonal = Dx[d] != 0 && Dy[d] != 0;
                    if (diagonal)
                    {
                        // Strict: a diagonal needs BOTH adjacent orthogonals
                        // clear, so a unit can never clip past the corner of a
                        // wall. The lenient rule (one side is enough) lets units
                        // shave wall corners, which in a castle game reads as
                        // walking through the wall. Units walk around instead.
                        if (!_map.Passable(cx + Dx[d], cy) || !_map.Passable(cx, cy + Dy[d]))
                            continue;
                    }

                    int neighbour = _map.Index(nx, ny);
                    if (_closedStamp[neighbour] == _stamp) continue;

                    // Terrain cost is the cost of ENTERING the tile; the diagonal
                    // surcharge scales it so marsh stays proportionally awkward.
                    int stepCost = _map.EnterCost(nx, ny);
                    if (diagonal) stepCost = stepCost * TileMap.DiagonalCost / TileMap.StepCost;

                    int tentative = _g[current] + stepCost;
                    bool seen = _seenStamp[neighbour] == _stamp;
                    if (seen && tentative >= _g[neighbour]) continue;

                    _g[neighbour] = tentative;
                    _cameFrom[neighbour] = current;
                    _seenStamp[neighbour] = _stamp;

                    int h = Heuristic(nx, ny, goalX, goalY);
                    Push(neighbour, tentative + h, h);
                }
            }

            return false;
        }

        // Octile distance in the same tenths as the step costs. Admissible: a
        // diagonal genuinely costs 14 and an orthogonal 10, so this never
        // overestimates on clear ground and never overestimates on marsh either
        // (marsh only ever costs more).
        static int Heuristic(int x, int y, int goalX, int goalY)
        {
            int dx = Math.Abs(x - goalX);
            int dy = Math.Abs(y - goalY);
            int min = Math.Min(dx, dy);
            return TileMap.StepCost * (dx + dy) - (2 * TileMap.StepCost - TileMap.DiagonalCost) * min;
        }

        void Reconstruct(int start, int goal, List<Tile> path)
        {
            for (int n = goal; n != start; n = _cameFrom[n])
                path.Add(new Tile(n % _map.Width, n / _map.Width));
            path.Reverse();
        }

        // ---- binary min-heap, ordered by (F, H, tile index) --------------------

        void Push(int node, int f, int h)
        {
            if (_heapCount == _heapNode.Length) Grow();

            int i = _heapCount++;
            _heapNode[i] = node;
            _heapF[i] = f;
            _heapH[i] = h;

            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!Less(i, parent)) break;
                Swap(i, parent);
                i = parent;
            }
        }

        int Pop()
        {
            int top = _heapNode[0];
            _heapCount--;

            if (_heapCount > 0)
            {
                _heapNode[0] = _heapNode[_heapCount];
                _heapF[0] = _heapF[_heapCount];
                _heapH[0] = _heapH[_heapCount];

                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1;
                    int right = left + 1;
                    int smallest = i;
                    if (left < _heapCount && Less(left, smallest)) smallest = left;
                    if (right < _heapCount && Less(right, smallest)) smallest = right;
                    if (smallest == i) break;
                    Swap(i, smallest);
                    i = smallest;
                }
            }
            return top;
        }

        // A TOTAL order: two distinct entries never compare equal, so the tile
        // A* expands next is fixed by the map, never by discovery order. Ties on
        // F break on H — preferring tiles closer to the goal, which also happens
        // to be faster — and any remaining tie breaks on the tile's index.
        bool Less(int a, int b)
        {
            if (_heapF[a] != _heapF[b]) return _heapF[a] < _heapF[b];
            if (_heapH[a] != _heapH[b]) return _heapH[a] < _heapH[b];
            return _heapNode[a] < _heapNode[b];
        }

        void Swap(int a, int b)
        {
            (_heapNode[a], _heapNode[b]) = (_heapNode[b], _heapNode[a]);
            (_heapF[a], _heapF[b]) = (_heapF[b], _heapF[a]);
            (_heapH[a], _heapH[b]) = (_heapH[b], _heapH[a]);
        }

        void Grow()
        {
            int size = _heapNode.Length * 2;
            Array.Resize(ref _heapNode, size);
            Array.Resize(ref _heapF, size);
            Array.Resize(ref _heapH, size);
        }
    }
}
