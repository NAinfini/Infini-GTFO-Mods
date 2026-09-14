import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import {pathToFileURL} from 'node:url';
import {auditAuthoringContracts} from './authoring-contract-audit.mjs';
const args = process.argv.slice(2);
if (args.length !== 2) throw Error('Usage: acceptance-vectors.mjs <website> <evidence>');
const [site, output] = args.map(value => path.resolve(value));
process.chdir(site);
await import(pathToFileURL(path.join(site,'Tools/register-typescript.ts')).href);
const load = name => import(pathToFileURL(path.join(site,'site/forge',name+'.ts')).href);
const {logicPrimitiveSeed} = await load('logic-primitives');
const {previewLogicPrimitive:preview} = await load('logic-preview');
const {resolveGraphContract,validateGraphMetadata} = await load('graph-schema');
const seed = logicPrimitiveSeed(); let assertions = 0;
const check = (value,name) => {assert.ok(value,name); assertions++;};
const write = (name,value) => fs.writeFileSync(path.join(output,name),JSON.stringify(value,null,2)+'\n');
const canonical = JSON.parse(fs.readFileSync(path.join(output,'sdk-canonical-manifest.json'),'utf8'));
for (const definition of seed.capabilities) validateGraphMetadata(definition.graph,definition.id);
write('authoring-registration-cases.json',auditAuthoringContracts(seed,canonical,check,{validateGraphMetadata,resolveGraphContract}));
// Every pure definition whose whole input side is variadic; the set and versions come from the website seed.
const variadic = seed.capabilities.filter(row => row.graph.execution === 'pure' && row.graph.variadic?.side === 'inputs');
check(variadic.length > 0, 'pure variadic definitions from the website seed');
const byName = new Map(variadic.map(row => [row.id.split('.').at(-1), row]));
assert.equal(byName.size, variadic.length, 'variadic definition names are unique');
const cases = [];
const add = (name, values, golden) => {
    const definition=byName.get(name);
    check(Boolean(definition),'website still defines variadic '+name);
    const {parameter} = definition.graph.variadic, output = definition.graph.outputs[0].id;
    const parameters={[parameter]:values.length};
    const graph=resolveGraphContract(definition.graph,parameters);
    check(graph.inputs.length===values.length,'resolved arity '+definition.id);
    const entries=graph.inputs.map((port,index)=>[port.id,values[index]]);
    const inputs=Object.fromEntries(entries), reverse=Object.fromEntries([...entries].reverse());
    const before=JSON.stringify(values);
    const result=preview(definition.id,parameters,inputs);
    assert.deepEqual(result.outputs,preview(definition.id,parameters,reverse).outputs); assertions++;
    const expected=result.outputs[output];
    if (golden!==undefined) {assert.deepEqual(expected,golden); assertions++;}
    check(JSON.stringify(values)===before,'source values unchanged '+definition.id);
    cases.push({name,capabilityId:definition.id,capabilityVersion:definition.version,parameters,portOrder:graph.inputs.map(p=>p.id),values,expected});
};
for (const count of [2,3,4,9,10,11,32]) {
    const numbers=Array.from({length:count},(_,i)=>i%4-1);
    for (const name of ['add','multiply','minimum','maximum']) add(name,numbers);
    add('all',Array(count).fill(true),true); add('all',[...Array(count-1).fill(true),false],false);
    add('any',Array(count).fill(false),false); add('any',[...Array(count-1).fill(false),true],true);
}
add('add',[1e16,1,-1e16],0); add('add',[1e16,-1e16,1],1);
add('multiply',[0,1e308,1e308],0);
add('minimum',[1e200,-1e200,0],-1e200); add('maximum',[1e200,-1e200,0],1e200);
check(new Set(cases.map(row=>row.name)).size===variadic.length,'every website pure variadic definition has shared values');
let graphRejections=0;
for (const definition of variadic) {
    const {parameter} = definition.graph.variadic;
    const count = definition.graph.parameters.find(p=>p.id===parameter);
    for (const value of [0,count.minimum-1,count.maximum+1,2.5]) {
        assert.throws(()=>resolveGraphContract(definition.graph,{[parameter]:value})); assertions++; graphRejections++;
    }
    const parameters={[parameter]:3};
    const resolved=resolveGraphContract(definition.graph,parameters);
    const sample=resolved.inputs[0].type==='boolean'?true:1;
    const inputs=Object.fromEntries(resolved.inputs.map(p=>[p.id,sample]));
    delete inputs[resolved.inputs[2].id];
    assert.throws(()=>preview(definition.id,parameters,inputs),/Missing runtime field/); assertions++; graphRejections++;
}
const sequence=seed.capabilities.find(row=>row.id==='forge.control.flow.sequence');
const steps=sequence.graph.parameters.find(p=>p.id===sequence.graph.variadic.parameter);
check(sequence.graph.variadic.side==='outputs','sequence expands its execution outputs');
check(resolveGraphContract(sequence.graph,{[steps.id]:steps.maximum}).outputs.length===steps.maximum,'sequence authoring ports resolve to the maximum, not runtime execution');
write('variadic-reference.json',{kind:'test-only-variadic-values',gameVerified:false,assertions,graphRejections,cases});
console.log(JSON.stringify({status:'passed',cases:cases.length,assertions,graphRejections}));
