import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import {pathToFileURL} from 'node:url';
import {createHash} from 'node:crypto';
const [website, destination] = process.argv.slice(2);
if (!website || !destination) throw Error('Usage: generate.mjs <website> <output.json>');
const site = path.resolve(website);
process.chdir(site);
await import(pathToFileURL(path.join(site, 'Tools/register-typescript.ts')).href);
const load = name => import(pathToFileURL(path.join(site, 'site/forge', name + '.ts')).href);
const {logicPrimitiveSeed} = await load('logic-primitives');
const {validateGraphMetadata, resolveGraphContract} = await load('graph-schema');
const {ForgeRegistry} = await load('registry');
const logic = logicPrimitiveSeed();
const registrations = [], resolutions = [];
const seedFor = definition => ({providers: [logic.providers.find(p => p.id === definition.owner)],
    capabilities: [definition], bindings: []});
function registration(name, definition, accepted) {
    const seed = seedFor(definition);
    if (accepted) new ForgeRegistry(seed);
    else assert.throws(() => new ForgeRegistry(seed), undefined, name);
    registrations.push({name, accepted, seed});
}
for (const definition of logic.capabilities) {
    validateGraphMetadata(definition.graph, definition.id);
    registration('actual:' + definition.id, definition, true);
}
const variable = logic.capabilities.filter(d => d.graph.variadic);
assert.equal(variable.length, 9, 'Re-review new variable definitions before updating this fixture');
for (const definition of variable) {
    assert.equal(definition.version, '1.1.0');
    const name = definition.id, spec = definition.graph.variadic;
    for (const count of [undefined, ...Array.from({length: 31}, (_, i) => i + 2), 0, 1, 33, -1, 2.5, null, '3', true]) {
        const parameters = count === undefined ? {} : {[spec.parameter]: count};
        const accepted = count === undefined || typeof count === 'number' && Number.isInteger(count) && count >= 2 && count <= 32;
        let expected = null;
        if (accepted) expected = resolveGraphContract(definition.graph, parameters);
        else assert.throws(() => resolveGraphContract(definition.graph, parameters));
        resolutions.push({name: name + ':' + String(count), seed: seedFor(definition),
            id: name, version: definition.version, parameters, accepted, expected});
    }
}
const base = variable.find(d => d.id === 'forge.modifier.value.add');
const mutations = {
    'unknown-side': g => {g.variadic.side = 'either';},
    'null-spec': g => {g.variadic = null;},
    'unknown-field': g => {g.variadic.default = 3;},
    'unknown-parameter': g => {g.variadic.parameter = 'missing';},
    'required-count': g => {g.parameters[0].required = true;},
    'value-role-count': g => {g.parameters[0].role = 'value';},
    'number-count': g => {g.parameters[0].type = 'number';},
    'missing-min': g => {delete g.parameters[0].minimum;},
    'missing-max': g => {delete g.parameters[0].maximum;},
    'wrong-min': g => {g.parameters[0].minimum = 3;},
    'max-is-base': g => {g.parameters[0].maximum = 2;},
    'max-over-budget': g => {g.parameters[0].maximum = 33;},
    'fractional-max': g => {g.parameters[0].maximum = 3.5;},
    'optional-template': g => {g.variadic.port.optional = true;},
    'nullable-template': g => {g.variadic.port.nullable = true;},
    'invalid-template-name': g => {g.variadic.port.id = '1bad';},
    'wrong-template-type': g => {g.variadic.port.type = 'boolean';},
    'unit-mismatch': g => {g.variadic.port.unit = 'HP';},
    'schema-mismatch': g => {g.variadic.port.schema = 'other';},
    'base-type-mismatch': g => {g.inputs[1].type = 'boolean';},
    'base-optional': g => {g.inputs[1].optional = true;},
    'base-nullable': g => {g.inputs[1].nullable = true;},
    'collision': g => {g.inputs[0].id = 'input_3';},
    'duplicate-base-id': g => {g.inputs[1].id = g.inputs[0].id;},
    'too-small-base': g => {g.inputs.pop(); g.parameters[0].minimum = 1;},
    'pure-execution-template': g => {g.variadic.port.type = 'execution';},
    'unknown-template-field': g => {g.variadic.port.default = 1;},
};
for (const [name, change] of Object.entries(mutations)) {
    const definition = structuredClone(base); change(definition.graph);
    registration('invalid:' + name, definition, false);
}
for (const count of [3, 7, 31]) {
    const definition = structuredClone(base), g = definition.graph;
    g.inputs = Array.from({length: count}, (_, i) => ({id: 'base_' + i, type: 'number'}));
    g.parameters[0].minimum = count; g.parameters[0].maximum = 32;
    registration('larger-base:' + count, definition, true);
    resolutions.push({name:'larger-base:' + count, seed:seedFor(definition), id:definition.id,
        version:definition.version, parameters:{input_count:32}, accepted:true,
        expected:resolveGraphContract(g, {input_count:32})});
}
const {compileForgeRuntimePlan, validateForgeRuntimePlan} = await load('runtime-compiler');
const {forgeRuntimeApiVersion} = await load('runtime-contracts');
const provider = {id:'test.graphports', kind:'extension', version:'1.0.0', dependencies:[]};
const make = (name, kind, graph) => ({id:provider.id+'.'+name, owner:provider.id,
    kind, label:name, version:'1.0.0', parameters:{}, graph});
const start = make('start', 'trigger', {domains:['logic'], execution:'host', inputs:[],
    outputs:[{id:'out',type:'execution'},{id:'target',type:'entity'}], parameters:[]});
// Every v0.2 action declares its recipient and result, so the repeated number block
// shares its side with a result output: a port group, not a whole-side variadic.
const action = make('action', 'action', {domains:['logic'], execution:'host',
    inputs:[{id:'in',type:'execution'},{id:'target',type:'entity'}],
    outputs:[{id:'value_1',type:'number'},{id:'value_2',type:'number'},{id:'result',type:'result',schema:'test.graphports.result'}],
    parameters:[{id:'output_count',type:'integer',role:'structural',required:false,minimum:2,maximum:32}],
    recipients:{input:'target',target:'entity',cardinality:'one',requires:[],result:'result'}});
const binding = (name, capability, role) => ({id:provider.id+'.binding.'+name,
    capabilityId:capability.id, providerId:provider.id, handler:name, role,
    status:'implemented', dependencies:[], requires:[]});
const fixedSeed = {providers:[provider], capabilities:[start,action],
    bindings:[binding('start',start,'observe'),binding('record',action,'execute')]};
const variableSeed = structuredClone(fixedSeed);
variableSeed.capabilities[1].graph.portGroups = [{id:'values',side:'outputs',parameter:'output_count',
    minimum:2,maximum:32,slots:[{id:'value',type:'number'}]}];
const limits = {maxEntrypoints:32,maxStepsPerEntrypoint:128,maxTotalSteps:512,
    maxEventsPerTick:128,maxCommandsPerTick:512,maxQueuedEvents:1024,maxCausalDepth:16};
const manifestFor = registry => ({schemaVersion:1,runtime:{id:'forge.runtime',version:'1.2.0',
    apiVersion:forgeRuntimeApiVersion,gameBuild:'synthetic-no-game'},registry,limits,
    bindingSupport:registry.bindings.map(b=>({bindingId:b.id,verification:'implementation-only',requiredPermissions:[]}))});
const fixedManifest = manifestFor(fixedSeed), variableManifest = manifestFor(variableSeed);
const graph = {schemaVersion:1,domain:'logic',authority:'host',entrypoints:['Start'],
    nodes:[{id:'Start',capabilityId:start.id,capabilityVersion:'1.0.0',bindingId:fixedSeed.bindings[0].id,parameters:{}},
        {id:'Action',capabilityId:action.id,capabilityVersion:'1.0.0',bindingId:fixedSeed.bindings[1].id,parameters:{output_count:3}}],
    edges:[{from:{node:'Start',port:'out'},to:{node:'Action',port:'in'}},
        {from:{node:'Start',port:'target'},to:{node:'Action',port:'target'}}]};
const options = {planId:'graph-port-expansion',resource:{id:'test.resource',revision:'1'},
    limits:{maxEventsPerTick:16,maxCommandsPerTick:16,maxQueuedEvents:32,maxCausalDepth:8},grantedPermissions:[]};
const fixedPlan = compileForgeRuntimePlan(graph,fixedManifest,options).plan;
validateForgeRuntimePlan(fixedPlan,fixedManifest,[]);
const variablePlan = compileForgeRuntimePlan(graph,variableManifest,options).plan;
validateForgeRuntimePlan(variablePlan,variableManifest,[]);
// output_count 3 inserts value_3 after the declared base block, before the result output.
const outputTypes = plan => plan.entrypoints[0].steps[0].layout.outputs.map(slot => slot.type);
assert.deepEqual(outputTypes(fixedPlan), [3, 3, 11]);
assert.deepEqual(outputTypes(variablePlan), [3, 3, 3, 11]);
assert.deepEqual(resolveGraphContract(variableSeed.capabilities[1].graph, {output_count:3}).outputs.map(p => p.id),
    ['value_1', 'value_2', 'value_3', 'result']);
// A layout written for the other contract never passes: the loader re-derives it.
assert.throws(()=>validateForgeRuntimePlan(fixedPlan,variableManifest,[]),/differs from its locked graph compilation/);
assert.throws(()=>validateForgeRuntimePlan(variablePlan,fixedManifest,[]),/differs from its locked graph compilation/);
const sourceFiles = fs.readdirSync(path.join(site,'site/forge')).filter(f=>f.endsWith('.ts'))
    .map(f=>path.join('site/forge',f)).concat(['Tools/register-typescript.ts']);
const hashes = Object.fromEntries(sourceFiles.map(f=>[f,
    createHash('sha256').update(fs.readFileSync(path.join(site,f))).digest('hex')]));
fs.writeFileSync(destination,JSON.stringify({schemaVersion:2,kind:'test-only-graph-contract-vectors',
    gameVerified:false,sourceHashes:hashes,registrations,resolutions,
    plans:{fixedSeed,variableSeed,fixedPlan,variablePlan}},null,2)+'\n');
console.log(JSON.stringify({registrations:registrations.length,resolutions:resolutions.length,
    websiteLayoutRefusals:2,sourceFiles:sourceFiles.length,gameVerified:false}));
