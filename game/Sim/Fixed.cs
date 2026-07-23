// Fixed.cs — Deterministic fixed-point math. A 1:1 port of prototype-node/src/fixed.js.
//
// The simulation NEVER uses `float`/`double`. Floating point can produce
// different results on different CPUs (ARM Mac vs x86 Ubuntu), which desyncs a
// lockstep match. We store real numbers as integers scaled by 2^16, so 1.0 is
// 65536. Integer math is bit-identical on every machine.
//
// Rendering (in the engine layer) may use float freely — only the SIMULATION
// is restricted.

namespace Sim
{
    public static class Fixed
    {
        public const int Shift = 16;
        public const int One = 1 << Shift; // 65536 == 1.0

        public static int FromInt(int n) => n * One;
        public static int ToInt(int f) => f / One; // truncates toward zero

        // All intermediates use `long` to avoid overflow; results truncate
        // toward zero to match the verified JS reference exactly.
        public static int Mul(int a, int b) => (int)((long)a * b / One);
        public static int Div(int a, int b) => (int)((long)a * One / b);

        // Deterministic integer square root (Newton's method).
        public static long Isqrt(long n)
        {
            if (n <= 0) return 0;
            long x = n;
            long y = (x + 1) / 2;
            while (y < x) { x = y; y = (x + n / x) / 2; }
            return x;
        }

        // Length of fixed-point vector (dx,dy), returned in fixed-point.
        public static int VLen(int dx, int dy)
        {
            long sq = (long)dx * dx / One + (long)dy * dy / One;
            return (int)Isqrt(sq * One);
        }
    }
}
