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
const {validateGraphMetadata, resolveGraphContract, validateGraphParameters} = await load('graph-schema');
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
const provider = {id:'test.graphports', kind:'extension', version:'1.0.0', dependencies:[]};
const make = (name, kind, graph) => ({id:provider.id+'.'+name, owner:provider.id,
    kind, label:name, version:'1.0.0', parameters:{}, graph});
const start = make('start', 'trigger', {domains:['logic'], execution:'host', inputs:[],
    outputs:[{id:'out',type:'execution'}], parameters:[]});
const action = make('action', 'action', {domains:['logic'], execution:'host',
    inputs:[{id:'in',type:'execution'}], outputs:[{id:'a',type:'number'},{id:'b',type:'number'}],
    parameters:[{id:'output_count',type:'integer',required:false,minimum:2,maximum:32}]});
const binding = (name, capability, role) => ({id:provider.id+'.binding.'+name,
    capabilityId:capability.id, providerId:provider.id, handler:name, role,
    status:'implemented', dependencies:[], requires:[]});
const v1seed = {providers:[provider], capabilities:[start,action],
    bindings:[binding('start',start,'observe'),binding('record',action,'execute')]};
const limits = {maxEntrypoints:32,maxStepsPerEntrypoint:128,maxTotalSteps:512,
    maxEventsPerTick:128,maxCommandsPerTick:512,maxQueuedEvents:1024,maxCausalDepth:16};
const manifest = {schemaVersion:1,runtime:{id:'forge.runtime',version:'1.2.0',
    apiVersion:'1.0.0',gameBuild:'synthetic-no-game'},registry:v1seed,limits,
    bindingSupport:v1seed.bindings.map(b=>({bindingId:b.id,verification:'implementation-only',requiredPermissions:[]}))};
const graph = {schemaVersion:1,domain:'logic',authority:'host',entrypoints:['Start'],
    nodes:[{id:'Start',capabilityId:start.id,capabilityVersion:'1.0.0',bindingId:v1seed.bindings[0].id,parameters:{}},
        {id:'Action',capabilityId:action.id,capabilityVersion:'1.0.0',bindingId:v1seed.bindings[1].id,parameters:{output_count:3}}],
    edges:[{from:{node:'Start',port:'out'},to:{node:'Action',port:'in'}}]};
const options = {planId:'v1-variable-guard',resource:{id:'test.resource',revision:'1'},
    limits:{maxEventsPerTick:16,maxCommandsPerTick:16,maxQueuedEvents:32,maxCausalDepth:8},grantedPermissions:[]};
const v1plan = compileForgeRuntimePlan(graph,manifest,options).plan;
validateForgeRuntimePlan(v1plan,manifest,[]);
const variableSeed = structuredClone(v1seed);
variableSeed.capabilities[1].graph.variadic = {side:'outputs',parameter:'output_count',port:{id:'value',type:'number'}};
const variableManifest = {...manifest,registry:variableSeed};
assert.throws(()=>compileForgeRuntimePlan(graph,variableManifest,options),/Variable ports/);
assert.throws(()=>validateForgeRuntimePlan(v1plan,variableManifest,[]),/Variable ports/);
const sourceFiles = fs.readdirSync(path.join(site,'site/forge')).filter(f=>f.endsWith('.ts'))
    .map(f=>path.join('site/forge',f)).concat(['Tools/register-typescript.ts']);
const hashes = Object.fromEntries(sourceFiles.map(f=>[f,
    createHash('sha256').update(fs.readFileSync(path.join(site,f))).digest('hex')]));
fs.writeFileSync(destination,JSON.stringify({schemaVersion:1,kind:'test-only-graph-contract-vectors',
    gameVerified:false,sourceHashes:hashes,registrations,resolutions,
    v1:{fixedSeed:v1seed,variableSeed,plan:v1plan}},null,2)+'\n');
console.log(JSON.stringify({registrations:registrations.length,resolutions:resolutions.length,
    websiteV1Refusals:2,sourceFiles:sourceFiles.length,gameVerified:false}));
