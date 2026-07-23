// Rng.cs — The simulation's only source of randomness.
//
// `System.Random` is banned in the sim: its algorithm is not contractually
// fixed, so two .NET versions may disagree, and it is seeded from the clock by
// default. Either would desync a match. This is a plain xorshift32 — a handful
// of integer shifts, identical on every CPU and every runtime, forever.
//
// THE STATE IS GAME STATE. The moment anything in the simulation draws from an
// Rng, its `State` has to be checksummed and carried in a MatchSnapshot like any
// other field. Two machines holding different generator states will agree for a
// while and then diverge at the first draw, which is the most confusing kind of
// desync to chase. Nothing in the sim draws from it yet; wire that up at the
// same moment you wire up the first random event.

namespace Sim
{
    public sealed class Rng
    {
        uint _state;

        // Seed 0 would make xorshift produce nothing but zeros forever, so it is
        // remapped rather than rejected — callers should not have to know that.
        public Rng(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

        public uint State => _state;
        public void Restore(uint state) => _state = state == 0 ? 0x9E3779B9u : state;

        public uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        // Unbiased by rejection rather than by modulo. A plain `% n` skews toward
        // low values, and a balance system built on a skewed die is subtly wrong
        // in a way nobody notices until they analyse a thousand fights.
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 1) return 0;

            uint bound = (uint)maxExclusive;
            uint limit = uint.MaxValue - (uint.MaxValue % bound) - 1;
            uint v;
            do { v = NextUInt(); } while (v > limit);
            return (int)(v % bound);
        }

        // Inclusive on both ends, which is how game rules are usually written
        // ("damage 3 to 5").
        public int NextInt(int minInclusive, int maxInclusive) =>
            minInclusive + NextInt(maxInclusive - minInclusive + 1);
    }
}
