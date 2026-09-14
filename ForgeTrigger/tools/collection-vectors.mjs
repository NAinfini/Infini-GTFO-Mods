import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import assert from 'node:assert/strict';

const args = process.argv.slice(2);
if (args.length !== 2) throw Error('Usage: collection-vectors.mjs <website> <output>');
const [site, output] = args.map(value => path.resolve(value));
process.chdir(site);
await import(pathToFileURL(path.join(site, 'Tools/register-typescript.ts')).href);
const {previewLogicPrimitive: preview} = await import(pathToFileURL(path.join(site, 'site/forge/logic-evaluator.ts')).href);
const {logicPrimitiveDefinitions} = await import(pathToFileURL(path.join(site, 'site/forge/logic-primitives.ts')).href);
const rows = []; let assertions = 0;
function check(value, name) { assert.ok(value, name); assertions++; }
const ref = (id, lifeEpoch = 1, worldEpoch = 1) => ({id, worldEpoch, lifeEpoch});
function add(name, parameters, inputs, golden, expectedCode, errorPattern) {
    const capabilityId = name === 'count' ? 'forge.condition.predicate.count' : 'forge.selector.target.' + name;
    const before = JSON.stringify(inputs); let result, error;
    try { result = preview(capabilityId, parameters, inputs); } catch (caught) { error = caught; }
    const row = {id: rows.length + '-' + name, capabilityId, parameters, inputs};
    if (expectedCode) {
        check(error instanceof Error && new RegExp(errorPattern).test(error.message), 'expected failure: ' + row.id + ': ' + error?.message);
        rows.push({...row, expectedCode, outcome: 'rejected'});
    } else {
        check(!error && result.kind === 'preview-only', 'existing preview: ' + row.id + ': ' + error);
        const value = result.outputs[name === 'count' ? 'value' : 'targets'];
        if (golden !== undefined) { assert.deepEqual(value, golden); assertions++; }
        assert.deepEqual(preview(capabilityId, parameters, inputs).outputs, result.outputs); assertions++;
        rows.push({...row, outcome: 'value', expected: value});
    }
    check(JSON.stringify(inputs) === before, 'inputs unchanged: ' + row.id);
}
const invalid = (name, p, i, code, pattern) => add(name, p, i, undefined, code, pattern);
const emit = {empty: 'emit-empty'};
const a = ref('test.entity:a'), b = ref('test.entity:b'), c = ref('test.entity:c');
const special = [a, ref(a.id, 2), ref(a.id, 10), ref(a.id, 100), ref(a.id, 1, 2),
    ref('test.entity:中'), ref('test.entity:😀'), ref('test.entity:"'), ref('test.entity:\\'),
    ref('test.entity:é'), ref('test.entity:e\u0301'), ref('test.entity:a\u2028b'), ref('test.entity:<&>'),
    ref('test.entity:a', 9007199254740991, 9007199254740991)];
for (const candidates of [[], [a], [b, a, b, c], special, [...special].reverse()]) {
    add('distinct', emit, {candidates});
    add('limit', emit, {candidates, max_targets: 2});
    for (const seed of [0, 1, 42, 2147483648, 4294967295]) {
        add('shuffle', emit, {candidates, seed});
        add('random', emit, {candidates, max_targets: 5, seed});
    }
    for (const operator of ['eq','ne','lt','lte','gt','gte']) add('count', {}, {candidates, operator, value: 2});
    for (const other of [[], [a,a,b], special]) {
        for (const name of ['union','intersection','difference']) add(name, emit, {a:candidates,b:other});
    }
}
add('distinct', emit, {candidates: [a,ref(a.id,2),ref(a.id,10),ref(a.id,100)]}, [ref(a.id,100),ref(a.id,10),a,ref(a.id,2)]);
add('limit', emit, {candidates:[c,b,a,c], max_targets:2}, [c,b]);
add('random', emit, {candidates:[a,a], max_targets:5, seed:42}, [a]);
invalid('limit', emit, {candidates:[a,ref(b.id,-1)], max_targets:1}, 'invalid-integer', 'epoch');
invalid('distinct', emit, {candidates:[ref('')]}, 'invalid-string', 'text');
invalid('distinct', emit, {candidates:[ref('test.entity:\u2028')]}, 'invalid-string', 'text');
invalid('distinct', emit, {candidates:[ref(' test.entity:a')]}, 'invalid-string', 'text');
invalid('distinct', emit, {candidates:[ref(a.id,9007199254740992)]}, 'invalid-integer', 'epoch');
for (const max_targets of [0,257,-1]) {
    invalid('limit', emit, {candidates:[], max_targets}, 'pure-selection-count', 'max_targets is outside 1…256');
    invalid('random', emit, {candidates:[a], max_targets, seed:42}, 'pure-selection-count', 'max_targets is outside 1…256');
}
for (const seed of [-1,4294967296]) {
    invalid('shuffle', emit, {candidates:[], seed}, 'pure-seed', 'seed is outside 0…4294967295');
    invalid('random', emit, {candidates:[a], seed, max_targets:1}, 'pure-seed', 'seed is outside 0…4294967295');
}
// The 2.0.0 count value is an unbounded integer input, so values outside the candidate budget still compare.
for (const [operator, value] of [['eq',-1],['gt',-1],['lt',4097],['eq',4097]]) add('count', {}, {candidates:[a], operator, value});
invalid('distinct', emit, {candidates:Array(4097).fill(a)}, 'pure-collection-budget', 'Preview target budget exceeded');
const large = Array.from({length:4096},(_,i)=>ref('test.entity:'+i));
invalid('union', emit, {a:large,b:[ref('test.entity:extra')]}, 'pure-collection-output-budget', 'Preview target budget exceeded');
add('count', {}, {candidates:large, operator:'eq', value:4096}, true);
add('limit', emit, {candidates:large, max_targets:256}, large.slice(0,256));
for (let trial=0;trial<24;trial++) {
    const candidates=Array.from({length:trial+3},(_,i)=>ref('test.entity:'+((i*7+trial)%17),i%3,trial%4));
    add('distinct', emit, {candidates}); add('random', emit, {candidates, max_targets:7, seed:trial});
    add('shuffle', emit, {candidates, seed:trial}); add('limit', emit, {candidates, max_targets:3});
}
const emptyResults = {distinct:{candidates:[]}, limit:{candidates:[],max_targets:1}, shuffle:{candidates:[],seed:1},
    random:{candidates:[],max_targets:1,seed:1}, union:{a:[],b:[]}, intersection:{a:[a],b:[b]}, difference:{a:[a],b:[a]}};
for (const [name, inputs] of Object.entries(emptyResults)) {
    add(name, {empty:'skip'}, inputs, []);
    invalid(name, {empty:'fail'}, inputs, 'pure-empty-selection', 'Empty selection rejected by its empty policy');
}
add('union', {empty:'fail'}, {a:[a],b:[]}, [a]);
const ids=[...new Set(rows.map(row=>row.capabilityId))].sort();
check(ids.length===8, 'eight existing collection primitives');
const versions = {};
for(const id of ids) {
    const definition=logicPrimitiveDefinitions.find(row=>row.id===id);
    check(definition?.graph.execution==='pure', 'website pure collection contract '+id+'@'+definition?.version);
    versions[id] = definition.version;
}
// The website does not preserve an identical endpoint; the C# helpers do. Recorded, not hidden.
const divergences = [];
for (const [name,inputs,expected] of [
    ['lerp', {from:0.1,to:0.1,factor:0.2}, 0.1],
    ['random_range', {minimum:1e100,maximum:1e100,seed:23}, 1e100]]) {
    const observed=preview('forge.modifier.value.'+name,{},inputs).outputs.value;
    divergences.push({name,inputs,expected,websiteActual:observed,websiteFixed:observed===expected});
}
const report={kind:'test-only-collection-vectors',gameVerified:false,assertions,canonicalIds:ids,versions,cases:rows};
fs.writeFileSync(path.join(output,'collections-reference.json'),JSON.stringify(report,null,2)+'\n');
fs.writeFileSync(path.join(output,'website-numeric-boundary.json'),JSON.stringify(divergences,null,2)+'\n');
console.log(JSON.stringify({status:'passed',assertions,cases:rows.length,primitiveCount:ids.length,
    websiteNumericBoundaryOpen:divergences.filter(row=>!row.websiteFixed).length}));
