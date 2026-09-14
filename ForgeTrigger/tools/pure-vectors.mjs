import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {createHash} from 'node:crypto';

const [root, site, output] = process.argv.slice(2).map(value => path.resolve(value));
if (!root || !site || !output) throw Error('Usage: node pure-vectors.mjs <ForgeTrigger> <website> <output>');
process.chdir(site);
await import(pathToFileURL(path.join(site, 'Tools/register-typescript.ts')).href);
const {previewLogicPrimitive} = await import(pathToFileURL(path.join(site, 'site/forge/logic-preview.ts')).href);
const {logicPrimitiveDefinitions} = await import(pathToFileURL(path.join(site, 'site/forge/logic-primitives.ts')).href);
const fixture = JSON.parse(fs.readFileSync(path.join(root, 'tests/fixtures/pure/cases.json'), 'utf8'));
if (fixture.kind !== 'test-only-pure-inputs' || fixture.schemaVersion !== 1) throw Error('Unexpected test fixture');
let assertions = 0;
const check = (value, name) => { if (!value) throw Error('FAIL: ' + name); assertions++; };
const ids = [...new Set(fixture.cases.map(row => row.capabilityId))].sort();
const definitions = ids.map(id => {
    const definition = logicPrimitiveDefinitions.find(row => row.id === id);
    // The version comes from the website definition; pure-authoring-definitions.json records exactly what was consumed.
    check(definition && definition.graph.execution === 'pure', 'website pure contract: ' + id + '@' + definition?.version);
    check(['condition', 'modifier'].includes(definition.kind), 'no object/control query: ' + id);
    return definition;
});
check(ids.length === 23, '23 scoped pure primitives, not full Trigger coverage');
const seen = new Set();
const rows = [];
for (const row of fixture.cases) {
    check(!seen.has(row.id), 'unique fixture ' + row.id); seen.add(row.id);
    const before = JSON.stringify({parameters:row.parameters, inputs:row.inputs});
    let result, error;
    try { result = previewLogicPrimitive(row.capabilityId, row.parameters, row.inputs); }
    catch (caught) { error = caught; }
    if (row.outcome === 'rejected') {
        check(error instanceof Error && new RegExp(row.errorPattern).test(error.message), 'expected rejection ' + row.id + ': ' + error?.message);
        rows.push({...row, message:error.message});
    } else {
        check(!error && result?.kind === 'preview-only', 'pure preview value ' + row.id + ': ' + error?.message);
        if (Object.hasOwn(row, 'golden')) check(JSON.stringify(result.outputs.value) === JSON.stringify(row.golden), 'mathematical golden ' + row.id);
        const repeat = previewLogicPrimitive(row.capabilityId, row.parameters, row.inputs);
        check(JSON.stringify(result.outputs) === JSON.stringify(repeat.outputs), 'deterministic repeated input ' + row.id);
        rows.push({...row, outputs:result.outputs});
    }
    check(before === JSON.stringify({parameters:row.parameters, inputs:row.inputs}), 'preview did not mutate inputs ' + row.id);
}
const definitionBytes = Buffer.from(JSON.stringify(definitions));
const report = {schemaVersion:1, kind:'test-only-pure-reference-results', status:'passed', gameVerified:false,
    assertions, canonicalIds:ids, vectorCases:rows.length, definitionSha256:createHash('sha256').update(definitionBytes).digest('hex'), cases:rows};
fs.writeFileSync(path.join(output, 'pure-reference.json'), JSON.stringify(report, null, 2) + String.fromCharCode(10));
fs.writeFileSync(path.join(output, 'pure-authoring-definitions.json'), JSON.stringify(definitions, null, 2) + String.fromCharCode(10));
console.log(JSON.stringify({status:report.status, assertions, vectorCases:rows.length, canonicalIds:ids.length}));
