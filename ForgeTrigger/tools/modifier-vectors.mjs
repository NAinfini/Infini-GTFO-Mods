import fs from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';

// Independent JavaScript reference for the pure modifier rows whose value preview the website does not implement
// yet. It recomputes every input in tests/fixtures/pure/modifier-cases.json, checks the recorded mathematical
// goldens, and writes the reference tests/Pure/ModifierVectorTests.cs replays against the C# helpers. The site is
// not consulted: these rows have no website evaluator, so the two independent implementations are this file and
// Pure/MappingNodes.cs and Pure/VectorNodes.cs.
const [root, output] = process.argv.slice(2).map(value => path.resolve(value));
if (!root || !output) throw Error('Usage: node modifier-vectors.mjs <ForgeTrigger> <output>');
const fixture = JSON.parse(fs.readFileSync(path.join(root, 'tests/fixtures/pure/modifier-cases.json'), 'utf8'));
if (fixture.kind !== 'test-only-modifier-inputs' || fixture.schemaVersion !== 1) throw Error('Unexpected test fixture');

let assertions = 0;
const check = (value, name) => { if (!value) throw Error('FAIL: ' + name); assertions++; };
const reject = (code, message) => { const error = new Error(message); error.code = code; throw error; };
const finite = value => { if (!Number.isFinite(value)) reject('pure-invalid-number', 'All inputs must be finite numbers.'); return value; };
const result = value => { if (!Number.isFinite(value)) reject('pure-nonfinite-result', 'Calculation overflowed or has no finite real result.'); return value === 0 ? 0 : value; };
const member = (members, name, parameter) => { const index = members.indexOf(name); if (index < 0) reject('pure-operation', 'Unknown ' + parameter + ' member: ' + name); return index; };
const vector = value => { if (!Array.isArray(value) || value.length !== 3) reject('pure-vector-shape', 'A metre vector has exactly three components.'); return value.map(finite); };
const near = (a, b) => Math.abs(a - b) <= 1e-12 * Math.max(1, Math.abs(a), Math.abs(b));
const same = (a, b) => Array.isArray(a) || Array.isArray(b) ? Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((n, i) => near(n, b[i])) : near(a, b);

const remap = (value, fromMinimum, fromMaximum, toMinimum, toMaximum) => {
    [value, fromMinimum, fromMaximum, toMinimum, toMaximum].forEach(finite);
    if (fromMinimum > fromMaximum) reject('pure-reversed-range', 'The source minimum must not exceed its maximum.');
    if (fromMinimum === fromMaximum) reject('pure-degenerate-range', 'The source interval must have a non-zero width.');
    const weight = (value - fromMinimum) / (fromMaximum - fromMinimum);
    return result(toMinimum + weight * (toMaximum - toMinimum));
};
const CURVES = ['linear', 'ease_in', 'ease_out', 'ease_in_out', 'step'];
const curve = (value, shape) => {
    finite(value);
    if (value < 0 || value > 1) reject('pure-curve-domain', 'A curve input must be within 0…1.');
    switch (CURVES[member(CURVES, shape, 'curve')]) {
        case 'linear': return result(value);
        case 'ease_in': return result(value * value);
        case 'ease_out': return result(1 - (1 - value) * (1 - value));
        case 'ease_in_out': return result(value < 0.5 ? 2 * value * value : 1 - 2 * (1 - value) * (1 - value));
        default: return result(value < 0.5 ? 0 : 1);
    }
};
const FALLOFFS = ['linear', 'quadratic', 'inverse_square', 'step'];
const falloff = (distance, radius, shape) => {
    finite(distance); finite(radius);
    if (distance < 0) reject('pure-distance-range', 'A falloff distance must not be negative.');
    if (radius <= 0) reject('pure-radius-range', 'A falloff radius must be positive.');
    if (distance > radius) reject('pure-falloff-range', 'A falloff distance must not exceed its radius.');
    const weight = distance / radius;
    switch (FALLOFFS[member(FALLOFFS, shape, 'curve')]) {
        case 'linear': return result(1 - weight);
        case 'quadratic': return result((1 - weight) * (1 - weight));
        case 'inverse_square': return result(1 / (1 + weight * weight));
        default: return result(weight < 1 ? 1 : 0);
    }
};
const scale = (count, value, minimum, port) => {
    finite(value);
    if (count < minimum) reject('pure-count-range', 'The ' + port + ' input must be at least ' + minimum + '.');
    return result(value * count);
};
const ZERO_POLICIES = ['reject', 'zero', 'forward'];
const normalize = (raw, policy) => {
    const a = vector(raw);
    const length = Math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
    if (length === 0) switch (ZERO_POLICIES[member(ZERO_POLICIES, policy, 'zero_policy')]) {
        case 'reject': reject('pure-zero-vector', 'A zero vector has no direction to normalize.');
        case 'zero': return [0, 0, 0];
        default: return [0, 0, 1];
    }
    if (!Number.isFinite(length)) reject('pure-nonfinite-result', 'The vector length overflowed and has no direction.');
    return [result(a[0] / length), result(a[1] / length), result(a[2] / length)];
};
const dot = (rawA, rawB) => { const a = vector(rawA), b = vector(rawB); return result(a[0] * b[0] + a[1] * b[1] + a[2] * b[2]); };
const cross = (rawA, rawB) => {
    const a = vector(rawA), b = vector(rawB);
    return [result(a[1] * b[2] - a[2] * b[1]), result(a[2] * b[0] - a[0] * b[2]), result(a[0] * b[1] - a[1] * b[0])];
};
const angle = (rawA, rawB) => {
    const a = vector(rawA), b = vector(rawB);
    if (a.every(n => n === 0) || b.every(n => n === 0)) reject('pure-zero-vector', 'The angle between directions needs two non-zero vectors.');
    const c = [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    const sine = Math.sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
    const cosine = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    if (!Number.isFinite(sine) || !Number.isFinite(cosine)) reject('pure-nonfinite-result', 'The angle calculation overflowed.');
    return result(Math.atan2(sine, cosine) * (180 / Math.PI));
};
// One binary64 weight as an exact integer in units of the smallest positive double: the same exact mass the C#
// side builds with BigInteger, so the two references weigh the authored values identically.
const mass = weight => {
    if (weight === 0) return 0n;
    const bits = new DataView(new ArrayBuffer(8));
    bits.setFloat64(0, weight, false);
    const raw = bits.getBigUint64(0, false);
    const exponent = Number((raw >> 52n) & 0x7ffn), fraction = raw & 0x000fffffffffffffn;
    return exponent === 0 ? fraction : (fraction | 0x0010000000000000n) << BigInt(exponent - 1);
};
const bitLength = value => { let length = 0; while (value > 0n) { length++; value >>= 1n; } return length; };
// The module's own seeded stream (Pure/SeededStream.cs), word for word: the third mix multiplies the masked
// value first and adds afterwards, and the two languages must consume the same words in the same order.
const seeded = seed => {
    if (!Number.isInteger(seed) || seed < 0 || seed > 0xffffffff) reject('pure-seed', 'Seed must be an explicit unsigned 32-bit integer.');
    let state = seed >>> 0;
    return () => {
        state = (state + 0x6d2b79f5) >>> 0;
        let value = Math.imul(state ^ (state >>> 15), 1 | state) >>> 0;
        value = (value ^ (Math.imul(value ^ (value >>> 7), 61 | value) + value)) >>> 0;
        return ((value ^ (value >>> 14)) >>> 0) / 4294967296;
    };
};
const weightedValue = (rawValues, rawWeights, seed) => {
    if (!Array.isArray(rawValues) || !Array.isArray(rawWeights)) reject('pure-set-shape', 'Weighted values and weights are collections.');
    if (rawValues.length !== rawWeights.length) reject('pure-set-shape', 'Weighted values and weights pair element for element, so both need the same count.');
    if (rawValues.length === 0) reject('pure-empty-set', 'A weighted pick needs at least one value and its weight.');
    // Each pair is validated as one unit, in list order: a value and its own weight before the next pair, so the
    // first fault in the list is the one reported.
    const values = [], masses = [];
    let total = 0n;
    for (let index = 0; index < rawValues.length; index++) {
        values[index] = finite(rawValues[index]);
        finite(rawWeights[index]);
        if (rawWeights[index] < 0) reject('pure-weight-range', 'A weight is a share of the total and must not be negative.');
        masses[index] = mass(rawWeights[index]); total += masses[index];
    }
    if (total === 0n) reject('pure-zero-weight', 'Every weight is zero, so no value has a share of the pick.');
    const next = seeded(seed);
    // The ticket is drawn with a fixed number of words and reduced modulo the total, so one evaluation consumes a
    // deterministic number of words instead of depending on which ticket a rejection would have discarded.
    const bits = bitLength(total), words = Math.ceil(bits / 32);
    let candidate = 0n;
    for (let index = 0; index < words; index++) {
        const remaining = bits - index * 32;
        const word = Math.floor(next() * 4294967296) >>> 0;
        candidate |= BigInt(remaining < 32 ? word & ((1 << remaining) - 1) : word) << BigInt(index * 32);
    }
    let ticket = candidate % total;
    for (let index = 0; index < values.length; index++) {
        if (ticket < masses[index]) return result(values[index]);
        ticket -= masses[index];
    }
    reject('pure-zero-weight', 'The weighted pick did not resolve to a value.');
};
const AGGREGATES = ['count', 'sum', 'average', 'minimum', 'maximum'];
const aggregate = (rawValues, operation) => {
    const memberIndex = member(AGGREGATES, operation, 'operation');
    if (!Array.isArray(rawValues)) reject('pure-set-shape', 'An aggregate reads a collection of numbers.');
    if (rawValues.length === 0 && AGGREGATES[memberIndex] !== 'count') reject('pure-empty-set', 'This aggregate has no value for an empty collection.');
    const values = rawValues.map(finite);
    switch (AGGREGATES[memberIndex]) {
        case 'count': return values.length;
        case 'sum': return result(values.reduce((total, value) => total + value, 0));
        case 'average': return result(values.reduce((total, value) => total + value, 0) / values.length);
        case 'minimum': return result(Math.min(...values));
        default: return result(Math.max(...values));
    }
};
const evaluate = row => {
    const p = row.parameters ?? {}, input = row.inputs ?? {};
    const v = key => finite(input[key]);
    switch (row.capabilityId) {
        case 'forge.modifier.value.remap': return remap(input.value, input.from_minimum, input.from_maximum, input.to_minimum, input.to_maximum);
        case 'forge.modifier.value.curve': return curve(v('value'), p.curve);
        case 'forge.modifier.value.falloff': return falloff(v('distance'), v('radius'), p.curve);
        case 'forge.modifier.value.by_count': return scale(input.count, v('value'), 0, 'count');
        case 'forge.modifier.value.by_players': return scale(input.players, v('value'), 1, 'players');
        case 'forge.modifier.value.by_stack': return scale(input.stacks, v('value'), 0, 'stacks');
        case 'forge.modifier.value.by_chain_hop': return scale(input.hop, v('value'), 1, 'hop');
        case 'forge.modifier.value.vector_normalize': return normalize(input.a, p.zero_policy);
        case 'forge.modifier.value.vector_dot': return dot(input.a, input.b);
        case 'forge.modifier.value.vector_cross': return cross(input.a, input.b);
        case 'forge.modifier.value.angle_between': return angle(input.a, input.b);
        case 'forge.modifier.value.weighted_value': return weightedValue(input.values, input.weights, input.seed);
        case 'forge.modifier.value.aggregate': return aggregate(input.values, p.operation);
        default: throw Error('Unknown modifier capability: ' + row.capabilityId);
    }
};

const seen = new Set();
const rows = [];
for (const row of fixture.cases) {
    check(!seen.has(row.id), 'unique fixture ' + row.id); seen.add(row.id);
    const before = JSON.stringify({parameters: row.parameters, inputs: row.inputs});
    let output, error;
    try { output = evaluate(row); } catch (caught) { error = caught; }
    if (row.outcome === 'rejected') {
        check(error instanceof Error && error.code === row.expectedCode, 'expected rejection ' + row.id + ': ' + error?.message);
        rows.push({...row, message: error.message});
    } else {
        check(!error, 'reference value ' + row.id + ': ' + error?.message);
        if (Object.hasOwn(row, 'golden')) check(same(output, row.golden), 'mathematical golden ' + row.id + ': ' + JSON.stringify(output));
        check(same(output, evaluate(row)), 'deterministic repeated input ' + row.id);
        rows.push({...row, outputs: {value: output}});
    }
    check(before === JSON.stringify({parameters: row.parameters, inputs: row.inputs}), 'reference did not mutate inputs ' + row.id);
}
const ids = [...new Set(fixture.cases.map(row => row.capabilityId))].sort();
const report = {schemaVersion: 1, kind: 'test-only-modifier-reference-results', status: 'passed', gameVerified: false,
    assertions, canonicalIds: ids, vectorCases: rows.length,
    fixtureSha256: createHash('sha256').update(fs.readFileSync(path.join(root, 'tests/fixtures/pure/modifier-cases.json'))).digest('hex'), cases: rows};
fs.writeFileSync(path.join(output, 'modifier-reference.json'), JSON.stringify(report, null, 2) + String.fromCharCode(10));
console.log(JSON.stringify({status: report.status, assertions, vectorCases: rows.length, canonicalIds: ids.length}));
