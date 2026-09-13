import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import {pathToFileURL} from 'node:url';
import {createHash} from 'node:crypto';
import {auditAuthoringContracts} from './authoring-contract-audit.mjs';

const [rootArg, siteArg, outputArg] = process.argv.slice(2);
if (!rootArg || !siteArg || !outputArg) throw Error('Usage: node t1-contracts.mjs <ForgeTrigger> <website> <evidence>');
const root = path.resolve(rootArg), site = path.resolve(siteArg), output = path.resolve(outputArg);
process.chdir(site);
await import(pathToFileURL(path.join(site, 'Tools/register-typescript.ts')).href);
const load = name => import(pathToFileURL(path.join(site, 'site/forge', name + '.ts')).href);
const {ForgeRegistry} = await load('registry');
const {validateForgeGraph, validateForgeAuthoringGraph} = await load('graph');
const {validateGraphMetadata, validateGraphParameters, resolveGraphContract} = await load('graph-schema');
const {compileForgeRuntimePlan, validateForgeRuntimePlan} = await load('runtime-compiler');
const {logicPrimitiveSeed} = await load('logic-primitives');
const read = name => JSON.parse(fs.readFileSync(name, 'utf8').replace(/^\uFEFF/, ''));
const write = (name, value) => fs.writeFileSync(path.join(output, name), JSON.stringify(value, null, 2) + '\n');
let assertions = 0;
const check = (ok, name) => {assert.ok(ok, name); assertions++; console.log('PASS: ' + name);};
const rejects = (fn, pattern, name) => {assert.throws(fn, new RegExp(pattern, 'i'), name); assertions++; console.log('PASS: ' + name);};
const suite = read(path.join(root, 'tests/fixtures/t1/cases.json'));
const manifest = read(path.join(output, 'sdk-manifest.json'));
check(manifest.runtime.gameBuild === 'synthetic-no-game', 'manifest is explicitly synthetic');
const registry = new ForgeRegistry(manifest.registry);
const grant = ['test.permission.record'];
const options = id => ({planId: 't1-' + id, resource: {id: 'test.resource', revision: 't1'},
    limits: {maxEventsPerTick: 16, maxCommandsPerTick: 16, maxQueuedEvents: 32, maxCausalDepth: 8}, grantedPermissions: grant});
const wires = [];
for (const row of suite.validGraphs) {
    const compiled = compileForgeRuntimePlan(row.graph, manifest, options(row.id));
    const roundtrip = validateForgeRuntimePlan(compiled.plan, manifest, grant);
    check(compiled.semanticJson === roundtrip.semanticJson, 'graph/plan roundtrip: ' + row.id);
    const reordered = structuredClone(row.graph); reordered.nodes.reverse(); reordered.edges.reverse();
    check(compileForgeRuntimePlan(reordered, manifest, options(row.id)).semanticJson === compiled.semanticJson,
        'enumeration order does not change linear plan: ' + row.id);
    wires.push({id: row.id, accepted: true, plan: compiled.plan, grants: grant});
}
for (const row of suite.invalidGraphs) {
    if (row.stage === 'compile') {
        check(validateForgeGraph(row.graph, registry).kind === 'validated-authoring-ir', 'valid authoring structure before lowering rejection: ' + row.id);
        rejects(() => compileForgeRuntimePlan(row.graph, manifest, options(row.id)), row.error, row.id);
    } else rejects(() => validateForgeGraph(row.graph, registry), row.error, row.id);
}
function mutate(input, changes) {
    const value = structuredClone(input);
    for (const {operation, path: keys, value: replacement} of changes) {
        const parent = keys.slice(0, -1).reduce((o, key) => o[key], value);
        const key = keys.at(-1);
        if (operation === 'delete') delete parent[key];
        else if (operation === 'set') parent[key] = structuredClone(replacement);
        else throw Error('Unknown test mutation: ' + operation);
    }
    return value;
}
for (const row of suite.invalidPlans) {
    const plan = mutate(wires[0].plan, row.changes), grants = row.grants ?? grant;
    rejects(() => validateForgeRuntimePlan(plan, manifest, grants), row.error, 'wire: ' + row.id);
    wires.push({id: row.id, accepted: false, plan, grants, code: row.code});
}
for (const name of ['branch', 'add']) {
    const plan = structuredClone(wires[0].plan);
    const binding = manifest.registry.bindings.find(b => b.id === 'test.trigger.binding.' + name);
    plan.bindings[1] = {bindingId: binding.id, capabilityId: binding.capabilityId, capabilityVersion: '1.0.0', providerId: 'test.trigger', providerVersion: '1.0.0', handler: name};
    plan.bindings.sort((a,b) => a.bindingId < b.bindingId ? -1 : 1);
    plan.entrypoints[0].steps[0].bindingId = binding.id;
    rejects(() => validateForgeRuntimePlan(plan, manifest, grant), 'Unsupported runtime node kind', 'wire cannot disguise ' + name + ' as action');
    wires.push({id: 'disguised-' + name, accepted: false, plan, grants: grant, code: 'node-kind'});
}
const renamed = structuredClone(manifest);
for (const c of renamed.registry.capabilities) c.label = 'Different display label';
check(compileForgeRuntimePlan(suite.validGraphs[0].graph, renamed, options('linear')).semanticJson === compileForgeRuntimePlan(suite.validGraphs[0].graph, manifest, options('linear')).semanticJson, 'labels do not create executable semantics');
const planned = structuredClone(manifest.registry);
planned.bindings.find(b => b.id === 'test.trigger.binding.record').status = 'planned';
rejects(() => new ForgeRegistry(planned).resolveUsage([{capabilityId:'test.trigger.record', bindingId:'test.trigger.binding.record'}]), 'not implemented', 'planned binding cannot resolve as executable');

// Consume the actual authoring definitions, not a copy of their semantics.
const logic = logicPrimitiveSeed();
write('authoring-seed.json', logic);
const authoringRegistry = new ForgeRegistry({providers: [...manifest.registry.providers, ...logic.providers],
    capabilities: [...manifest.registry.capabilities, ...logic.capabilities], bindings: manifest.registry.bindings});
check(logic.bindings.length === 0, 'actual logic definitions have no runtime bindings');
for (const definition of logic.capabilities) {
    validateGraphMetadata(definition.graph, definition.id);
    check(definition.graph != null, 'actual typed graph metadata: ' + definition.id);
}
const canonical = read(path.join(output, 'sdk-canonical-manifest.json'));
write('authoring-registration-cases.json', auditAuthoringContracts(logic, canonical, check, {validateGraphMetadata, resolveGraphContract}));
const constantGraph = structuredClone(suite.validGraphs[0].graph);
constantGraph.nodes.push({id:'Constant', capabilityId:'forge.modifier.value.constant', capabilityVersion:'1.0.0', bindingId:'', parameters:{value:5}});
constantGraph.edges.find(e => e.to.port === 'value').from = {node:'Constant', port:'value'};
check(validateForgeAuthoringGraph(constantGraph, authoringRegistry).kind === 'validated-authoring-ir', 'real canonical constant authors in the existing IR');
rejects(() => validateForgeGraph(constantGraph, authoringRegistry), 'Binding/capability mismatch', 'authoring constant does not become a runtime binding');
const constant = logic.capabilities.find(c => c.id === 'forge.modifier.value.constant');
rejects(() => validateGraphParameters({value: Infinity}, constant.graph.parameters, 'constant'), 'numeric parameter', 'non-finite authoring parameter rejected');
const branchGraph = structuredClone(suite.invalidGraphs.find(r => r.id === 'control-needs-r4').graph);
Object.assign(branchGraph.nodes.find(n => n.id === 'Branch'), {capabilityId:'forge.control.flow.branch', bindingId:''});
check(validateForgeAuthoringGraph(branchGraph, authoringRegistry).kind === 'validated-authoring-ir', 'real canonical branch authors without inventing bindings');
rejects(() => validateForgeGraph(branchGraph, authoringRegistry), 'Binding/capability mismatch', 'canonical branch remains unbound');
write('canonical-authoring.graph-cases.json', [constantGraph, branchGraph]);

const catalogFile = path.join(site, 'catalog/capability-catalog.json');
const catalog = read(catalogFile);
const nativeFile = path.join(site, 'Tests/Forge/fixtures/runtime/native-manifest.json');
const native = read(nativeFile);
const known = new Map(logic.capabilities.map((c,i) => [c.id, {definition:c, source:'site/forge/logic-primitives.ts', index:i, evidence:'authoring-contract-only'}]));
for (const [index,c] of native.registry.capabilities.entries())
    if (!known.has(c.id)) known.set(c.id, {definition:c,source:'Tests/Forge/fixtures/runtime/native-manifest.json',index,evidence:'recorded-fixture-not-current-game-verification'});
const prerequisites = {trigger:['R3','domain facts'],selector:['R3','R4'],condition:['R3','R4'],modifier:['R4'],control:['R4','R6'],variable:['R4','R6'],state:['R6'],event:['R3','R4','R5']};
const rows = catalog.canonicalVocabulary.filter(r => r.category !== 'action').map(row => {
    const match = known.get(row.id), definition = match?.definition;
    return {id:row.id,category:row.category,catalogPointer:'/canonicalVocabulary/' + catalog.canonicalVocabulary.indexOf(row),
        plannedStatus:row.runtimeStatus, triggerRuntimeBinding:null, requiredPrimitives:prerequisites[row.category],
        authoring:match ? {source:match.source,index:match.index,version:definition.version,evidence:match.evidence,graph:definition.graph} : null,
        domainDifference:definition?.graph ? {catalogOnly:row.domains.filter(d=>!definition.graph.domains.includes(d)),typedOnly:definition.graph.domains.filter(d=>!row.domains.includes(d))} : null,
        lifecycle:'No new executable Trigger binding; see T1-CONTRACT-MAP.md for owner/scope/epoch requirements'};
});
const counts = {};
for (const row of rows) counts[row.category] = (counts[row.category] ?? 0) + 1;
check(new Set(catalog.canonicalVocabulary.map(r=>r.id)).size === catalog.canonicalVocabulary.length, 'catalog canonical IDs unique');
check(rows.length + catalog.canonicalVocabulary.filter(r=>r.category === 'action').length === catalog.canonicalVocabulary.length, 'complete base vocabulary mapping, actions separately retained');
const sha256 = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const missingFromCatalog = logic.capabilities.filter(c=>!catalog.canonicalVocabulary.some(r=>r.id===c.id)).map(c=>c.id);
const report = {kind:'generated-non-executable-source-audit', schemaVersion:1, sourceCatalogSha256:sha256(catalogFile),
    sourceLogicSha256:sha256(path.join(site,'site/forge/logic-primitives.ts')), sourceNativeFixtureSha256:sha256(nativeFile),
    catalogTotal:catalog.canonicalVocabulary.length, baseNodeCount:rows.length, counts, authoringDefinitionCount:logic.capabilities.length,
    authoringDefinitionsMissingFromCatalog:missingFromCatalog, triggerExecutableBindings:0, rows};
write('node-audit.json', report);
const provenanceOnly = structuredClone(wires[0].plan);
provenanceOnly.resource.revision = 'unresolved-resource-revision';
check(validateForgeRuntimePlan(provenanceOnly, manifest, grant).kind === 'compiled-runtime-plan', 'v1 resource revision is provenance, not resource-catalog validation');
wires.push({id:'resource-revision-provenance-only', accepted:true, plan:provenanceOnly, grants:grant});
write('wire-cases.json', {schemaVersion:1,evidence:'synthetic-no-game', cases:wires});
write('typescript-result.json', {assertions,status:'passed',gameVerified:false,graphNegativeCases:suite.invalidGraphs.length,sharedWireCases:wires.length});
console.log(JSON.stringify({status:'passed',assertions,baseNodes:rows.length,typedAuthoringNodes:logic.capabilities.length,missingFromCatalog,wireCases:wires.length}));
