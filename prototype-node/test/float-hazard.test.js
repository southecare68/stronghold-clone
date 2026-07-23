// float-hazard.test.js — WHY we forbid floating point in the simulation.
//
// We can't reproduce a real cross-CPU float divergence on one machine (same
// FPU), but we CAN show the underlying hazard directly: floating-point results
// depend on the ORDER of operations, while integer/fixed-point results do not.
//
// In a real RTS, two players' machines can end up summing forces, damage, or
// positions in slightly different orders (different unit iteration, compiler
// optimizations, SSE vs x87, ARM vs x86). With floats, that tiny difference
// compounds tick after tick until the games visibly disagree — a desync. With
// fixed-point integers, order does not matter, so it never happens.

function floatSumOrderMatters() {
  // A set of values chosen so association order changes the float result.
  const vals = [0.1, 0.2, 0.3, 1e16, -1e16, 0.4, 0.5];
  let fwd = 0;
  for (let i = 0; i < vals.length; i++) fwd += vals[i];
  let rev = 0;
  for (let i = vals.length - 1; i >= 0; i--) rev += vals[i];
  return { fwd, rev, equal: fwd === rev };
}

function intSumOrderIndependent() {
  // Same experiment in the integer domain (values scaled to fixed-point).
  const vals = [100, 200, 300, 400, 500, -700, 900];
  let fwd = 0;
  for (let i = 0; i < vals.length; i++) fwd += vals[i];
  let rev = 0;
  for (let i = vals.length - 1; i >= 0; i--) rev += vals[i];
  return { fwd, rev, equal: fwd === rev };
}

const f = floatSumOrderMatters();
const n = intSumOrderIndependent();

console.log('=== Why the sim avoids floating point ===');
console.log(`float sum forward:      ${f.fwd}`);
console.log(`float sum reversed:     ${f.rev}`);
console.log(`float order-independent: ${f.equal ? 'yes' : 'NO ✗  <-- same math, different result = desync risk'}`);
console.log('');
console.log(`integer sum forward:    ${n.fwd}`);
console.log(`integer sum reversed:   ${n.rev}`);
console.log(`integer order-independent: ${n.equal ? 'YES ✓  <-- safe for lockstep' : 'no'}`);

if (f.equal || !n.equal) {
  console.log('RESULT: unexpected');
  process.exit(1);
}
console.log('RESULT: PASS (floats are order-sensitive; integers are not)');
