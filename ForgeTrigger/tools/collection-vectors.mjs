import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import assert from 'node:assert/strict';

const args = process.argv.slice(2);
if (args.length !== 2) throw Error('Usage: collection-vectors.mjs <website> <output>');
const [site, output] = args.map(value => path.resolve(value));
process.chdir(site);
await import(pathToFileURL(path.join(site, 'Tools/register-typescript.ts')).href);
const {previewLogicPrimitive: preview} = await import(pathToFileURL(path.join(site, 'site/forge/logic-preview.ts')).href);
const {logicPrimitiveDefinitions} = await import(pathToFileURL(path.join(site, 'site/forge/logic-primitives.ts')).href);
const rows = []; let assertions = 0;
function check(value, name) { assert.ok(value, name); assertions++; }
const ref = (id, lifeEpoch = 1, worldEpoch = 1) => ({id, worldEpoch, lifeEpoch});
const key = value => JSON.stringify([value.worldEpoch, value.id, value.lifeEpoch]);
function add(name, parameters, inputs, golden, expectedCode, errorPattern) {
    const capabilityId = name === 'count' ? 'forge.condition.predicate.count' : 'forge.selector.target.' + name;
    const before = JSON.stringify(inputs); let result, error;
    try { result = preview(capabilityId, parameters, inputs); } catch (caught) { error = caught; }
    const row = {id: rows.length + '-' + name, capabilityId, parameters, inputs};
    if (expectedCode) {
        check(error instanceof Error && new RegExp(errorPattern).test(error.message), 'expected failure: ' + row.id);
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
const a = ref('test.entity:a'), b = ref('test.entity:b'), c = ref('test.entity:c');
const special = [a, ref(a.id, 2), ref(a.id, 10), ref(a.id, 100), ref(a.id, 1, 2),
    ref('test.entity:中'), ref('test.entity:😀'), ref('test.entity:"'), ref('test.entity:\\'),
    ref('test.entity:é'), ref('test.entity:e\u0301'), ref('test.entity:a\u2028b'), ref('test.entity:<&>'),
    ref('test.entity:a', 9007199254740991, 9007199254740991)];
for (const targets of [[], [a], [b, a, b, c], special, [...special].reverse()]) {
    add('distinct', {}, {targets});
    add('limit', {count: 2}, {targets});
    for (const seed of [0, 1, 42, 2147483648, 4294967295]) {
        add('shuffle', {seed}, {targets});
        add('random', {seed, count: 5}, {targets});
    }
    for (const operator of ['eq','ne','lt','lte','gt','gte']) add('count', {operator, count: 2}, {targets});
    for (const other of [[], [a,a,b], special]) {
        for (const name of ['union','intersection','difference']) add(name, {}, {a:targets,b:other});
    }
}
add('distinct', {}, {targets: [a,ref(a.id,2),ref(a.id,10),ref(a.id,100)]}, [ref(a.id,100),ref(a.id,10),a,ref(a.id,2)]);
add('limit', {count:2}, {targets:[c,b,a,c]}, [c,b]);
add('random', {count:5,seed:42}, {targets:[a,a]}, [a]);
const invalid = (name, p, i, code, pattern) => add(name, p, i, undefined, code, pattern);
invalid('limit', {count:1}, {targets:[a,ref(b.id,-1)]}, 'invalid-integer', 'epoch');
invalid('distinct', {}, {targets:[ref('')]}, 'invalid-string', 'text');
invalid('distinct', {}, {targets:[ref('test.entity:\u2028')]}, 'invalid-string', 'text');
invalid('distinct', {}, {targets:[ref(' test.entity:a')]}, 'invalid-string', 'text');
invalid('distinct', {}, {targets:[ref(a.id,9007199254740992)]}, 'invalid-integer', 'epoch');
for (const count of [0,257,-1]) {
    invalid('limit', {count}, {targets:[]}, 'pure-selection-count', 'outside bounds');
    invalid('random', {count,seed:42}, {targets:[a]}, 'pure-selection-count', 'outside bounds');
}
for (const seed of [-1,4294967296]) {
    invalid('shuffle', {seed}, {targets:[]}, 'pure-seed', 'outside bounds');
    invalid('random', {seed,count:1}, {targets:[a]}, 'pure-seed', 'outside bounds');
}
invalid('count', {operator:'eq',count:-1}, {targets:[]}, 'pure-count-range', 'outside bounds');
invalid('count', {operator:'eq',count:4097}, {targets:[]}, 'pure-count-range', 'outside bounds');
invalid('distinct', {}, {targets:Array(4097).fill(a)}, 'pure-collection-budget', 'budget');
const large = Array.from({length:4096},(_,i)=>ref('test.entity:'+i));
invalid('union', {}, {a:large,b:[ref('test.entity:extra')]}, 'pure-collection-output-budget', 'budget');
add('count', {operator:'eq',count:4096}, {targets:large}, true);
add('limit', {count:256}, {targets:large}, large.slice(0,256));
for (let trial=0;trial<24;trial++) {
    const targets=Array.from({length:trial+3},(_,i)=>ref('test.entity:'+((i*7+trial)%17),i%3,trial%4));
    add('distinct', {}, {targets}); add('random', {seed:trial,count:7}, {targets});
    add('shuffle', {seed:trial}, {targets}); add('limit', {count:3}, {targets});
}
const ids=[...new Set(rows.map(row=>row.capabilityId))].sort();
check(ids.length===8, 'eight existing collection primitives');
for(const id of ids) {
    const definition=logicPrimitiveDefinitions.find(row=>row.id===id);
    const expectedVersion=['forge.selector.target.union','forge.selector.target.intersection'].includes(id)?'1.1.0':'1.0.0';
    check(definition?.version===expectedVersion&&definition.graph.execution==='pure', 'exact reviewed collection contract '+id+'@'+expectedVersion);
}
const divergences = [];
for (const [name,parameters,inputs,expected] of [
    ['lerp',{}, {a:0.1,b:0.1,weight:0.2}, 0.1],
    ['random_range',{minimum:1e100,maximum:1e100,seed:23},{},1e100]]) {
    const observed=preview('forge.modifier.value.'+name,parameters,inputs).outputs.value;
    divergences.push({name,parameters,inputs,expected,websiteActual:observed,websiteFixed:observed===expected});
}
const report={kind:'test-only-collection-vectors',gameVerified:false,assertions,canonicalIds:ids,cases:rows};
fs.writeFileSync(path.join(output,'collections-reference.json'),JSON.stringify(report,null,2)+'\n');
fs.writeFileSync(path.join(output,'website-numeric-boundary.json'),JSON.stringify(divergences,null,2)+'\n');
console.log(JSON.stringify({status:'passed',assertions,cases:rows.length,primitiveCount:ids.length,
    websiteNumericBoundaryOpen:divergences.filter(row=>!row.websiteFixed).length}));
