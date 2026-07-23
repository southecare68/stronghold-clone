// fixed.js — Deterministic fixed-point math.
//
// WHY: A multiplayer RTS uses "lockstep" — every player's machine runs the
// SAME simulation and they exchange only commands. For that to work, the math
// must produce BIT-IDENTICAL results on every CPU. Regular floating point does
// NOT guarantee that across different hardware/compilers, so we never use it in
// the simulation. Instead we represent all real numbers as integers scaled by a
// fixed factor (fixed-point). Integer math is exactly reproducible everywhere.
//
// We use a shift of 16 bits: the value 1.0 is stored as 65536.
// JS numbers are float64, but integers up to 2^53 are exact, and we only ever
// use integer operations here — so this is a faithful stand-in for the C#
// `int`/`long` fixed-point you'll use in the engine.

const SHIFT = 16;
const ONE = 1 << SHIFT; // 65536 == 1.0

const fromInt = (n) => (n * ONE) | 0 === (n * ONE) ? n * ONE : n * ONE; // n -> fixed
const toInt = (f) => Math.trunc(f / ONE); // fixed -> whole number (truncated)

// Multiply two fixed-point numbers. Product is scaled by ONE^2, so shift back.
// Use Math.trunc to keep it a deterministic integer (no banker's rounding).
const mul = (a, b) => Math.trunc((a * b) / ONE);

// Divide two fixed-point numbers.
const div = (a, b) => Math.trunc((a * ONE) / b);

// Deterministic integer square root (Newton's method on integers).
// Same inputs -> same output on any machine. Used for distances.
function isqrt(n) {
  if (n <= 0) return 0;
  let x = n;
  let y = Math.trunc((x + 1) / 2);
  while (y < x) {
    x = y;
    y = Math.trunc((x + Math.trunc(n / x)) / 2);
  }
  return x;
}

// Length of a fixed-point vector (dx, dy), returned in fixed-point.
// dist^2 can be large; we compute in the integer domain then sqrt.
function vlen(dx, dy) {
  // (dx/ONE)^2 + (dy/ONE)^2, kept in fixed-point: sqrt(dx*dx + dy*dy)
  const sq = Math.trunc((dx * dx) / ONE) + Math.trunc((dy * dy) / ONE);
  return isqrt(sq * ONE); // scale so result is in fixed-point units
}

module.exports = { SHIFT, ONE, fromInt, toInt, mul, div, isqrt, vlen };
