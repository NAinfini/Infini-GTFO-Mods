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
const {validateGraphMetadata, resolveGraphContract, normalizePortGroups} = await load('graph-schema');
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
// Every repeat form the website declares (whole-side variadic and port groups), with its own version and bounds.
const variable = logic.capabilities.filter(d => normalizePortGroups(d.graph).length > 0);
assert.ok(variable.length > 0, 'The website declares no variable port definitions');
for (const definition of variable) {
    for (const group of normalizePortGroups(definition.graph)) {
        const {parameter, minimum, maximum} = group;
        const valid = Array.from({length: maximum - minimum + 1}, (_, i) => minimum + i);
        for (const count of [undefined, ...valid, minimum - 2, minimum - 1, maximum + 1, -1, minimum + 0.5, null, String(minimum + 1), true]) {
            const parameters = count === undefined ? {} : {[parameter]: count};
            const accepted = count === undefined || Number.isInteger(count) && count >= minimum && count <= maximum;
            let expected = null;
            if (accepted) expected = resolveGraphContract(definition.graph, parameters);
            else assert.throws(() => resolveGraphContract(definition.graph, parameters));
            resolutions.push({name: definition.id + ':' + parameter + ':' + String(count), seed: seedFor(definition),
                id: definition.id, version: definition.version, parameters, accepted, expected});
        }
    }
}
// Whole-side mutations start from a pure numeric variadic; group mutations from a declared port group.
const base = variable.find(d => d.graph.execution === 'pure' && d.graph.variadic?.side === 'inputs' && d.graph.variadic.port.type === 'number');
assert.ok(base, 'No pure numeric input variadic to mutate');
const count = g => g.parameters.find(p => p.id === g.variadic.parameter);
const other = type => type === 'boolean' ? 'number' : 'boolean';
const mutations = {
    'unknown-side': g => {g.variadic.side = 'either';},
    'null-spec': g => {g.variadic = null;},
    'unknown-field': g => {g.variadic.default = 3;},
    'unknown-parameter': g => {g.variadic.parameter = 'missing';},
    'required-count': g => {count(g).required = true;},
    'value-role-count': g => {count(g).role = 'value';},
    'number-count': g => {count(g).type = 'number';},
    'missing-min': g => {delete count(g).minimum;},
    'missing-max': g => {delete count(g).maximum;},
    'wrong-min': g => {count(g).minimum = g.inputs.length + 1;},
    'max-is-base': g => {count(g).maximum = g.inputs.length;},
    'max-over-budget': g => {count(g).maximum += 1;},
    'fractional-max': g => {count(g).maximum -= 0.5;},
    'optional-template': g => {g.variadic.port.optional = true;},
    'nullable-template': g => {g.variadic.port.nullable = true;},
    'invalid-template-name': g => {g.variadic.port.id = '1bad';},
    'wrong-template-type': g => {g.variadic.port.type = other(g.variadic.port.type);},
    'unit-mismatch': g => {g.variadic.port.unit = 'HP';},
    'schema-mismatch': g => {g.variadic.port.schema = 'other';},
    'base-type-mismatch': g => {g.inputs[1].type = other(g.inputs[1].type);},
    'base-optional': g => {g.inputs[1].optional = true;},
    'base-nullable': g => {g.inputs[1].nullable = true;},
    'collision': g => {g.inputs[0].id = g.variadic.port.id + '_' + (g.inputs.length + 1);},
    'duplicate-base-id': g => {g.inputs[1].id = g.inputs[0].id;},
    'too-small-base': g => {g.inputs.length = 1; count(g).minimum = 1;},
    'pure-execution-template': g => {g.variadic.port.type = 'execution';},
    'unknown-template-field': g => {g.variadic.port.default = 1;},
};
for (const [name, change] of Object.entries(mutations)) {
    const definition = structuredClone(base); change(definition.graph);
    registration('invalid:' + name, definition, false);
}
const grouped = variable.find(d => d.graph.portGroups);
if (grouped) {
    const first = g => g.portGroups[0];
    const groupMutations = {
        'group-minimum-mismatch': g => {first(g).minimum += 1;},
        'group-maximum-mismatch': g => {first(g).maximum -= 1;},
        'group-unknown-parameter': g => {first(g).parameter = 'missing';},
        'group-unknown-field': g => {first(g).default = 3;},
        'group-empty-slots': g => {first(g).slots = [];},
        'group-base-renamed': g => {g[first(g).side].find(p => p.id === first(g).slots[0].id + '_1').id = 'renamed';},
    };
    for (const [name, change] of Object.entries(groupMutations)) {
        const definition = structuredClone(grouped); change(definition.graph);
        registration('invalid:' + name, definition, false);
    }
}
{
    const spec = count(base.graph);
    for (const size of [spec.minimum + 1, Math.floor((spec.minimum + spec.maximum) / 2), spec.maximum - 1]) {
        const definition = structuredClone(base), g = definition.graph;
        g.inputs = Array.from({length: size}, (_, i) => ({id: 'base_' + i, type: g.variadic.port.type}));
        count(g).minimum = size;
        registration('larger-base:' + size, definition, true);
        const parameters = {[g.variadic.parameter]: spec.maximum};
        resolutions.push({name:'larger-base:' + size, seed:seedFor(definition), id:definition.id,
            version:definition.version, parameters, accepted:true, expected:resolveGraphContract(g, parameters)});
    }
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
    gameVerified:false,sourceHashes:hashes,variableDefinitions:variable.map(d=>d.id+'@'+d.version),
    registrations,resolutions,plans:{fixedSeed,variableSeed,fixedPlan,variablePlan}},null,2)+'\n');
console.log(JSON.stringify({registrations:registrations.length,resolutions:resolutions.length,
    variableDefinitions:variable.length,websiteLayoutRefusals:2,sourceFiles:sourceFiles.length,gameVerified:false}));
