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
const cases = [];
const ids = ['all','any','add','multiply','minimum','maximum','union','intersection'];
const idFor = name => (['all','any'].includes(name) ? 'forge.condition.predicate.' : ['union','intersection'].includes(name) ? 'forge.selector.target.' : 'forge.modifier.value.')+name;
const ref = (id,lifeEpoch=1) => ({id:'test.variadic:'+id,worldEpoch:1,lifeEpoch});
function add(name, values, golden) {
    const id=idFor(name), definition=seed.capabilities.find(row=>row.id===id);
    check(definition?.version==='1.1.0','exact variadic 1.1.0 pin '+id);
    const parameters={input_count:values.length};
    const graph=resolveGraphContract(definition.graph,parameters);
    check(graph.inputs.length===values.length,'resolved arity '+id);
    const entries=graph.inputs.map((port,index)=>[port.id,values[index]]);
    const inputs=Object.fromEntries(entries), reverse=Object.fromEntries([...entries].reverse());
    const before=JSON.stringify(values);
    const result=preview(id,parameters,inputs);
    assert.deepEqual(result.outputs,preview(id,parameters,reverse).outputs); assertions++;
    const expected=result.outputs[['union','intersection'].includes(name)?'targets':'value'];
    if (golden!==undefined) {assert.deepEqual(expected,golden); assertions++;}
    check(JSON.stringify(values)===before,'source values unchanged '+id);
    cases.push({name,capabilityId:id,capabilityVersion:'1.1.0',parameters,portOrder:graph.inputs.map(p=>p.id),values,expected});
}
for (const count of [2,3,4,9,10,11,32]) {
    const numbers=Array.from({length:count},(_,i)=>i%4-1);
    for (const name of ['add','multiply','minimum','maximum']) add(name,numbers);
    add('all',Array(count).fill(true),true); add('all',[...Array(count-1).fill(true),false],false);
    add('any',Array(count).fill(false),false); add('any',[...Array(count-1).fill(false),true],true);
    const lists=Array.from({length:count},(_,i)=>[ref('same'),ref('same',2),ref('item-'+i),ref('same')]);
    add('union',lists); add('intersection',lists,[ref('same'),ref('same',2)]);
}
add('add',[1e16,1,-1e16],0); add('add',[1e16,-1e16,1],1);
add('multiply',[0,1e308,1e308],0);
add('minimum',[1e200,-1e200,0],-1e200); add('maximum',[1e200,-1e200,0],1e200);
add('union',[[],[],[]],[]); add('intersection',[[ref('a')],[],[ref('a')]],[]);
let graphRejections=0;
for (const name of ids) {
    const id=idFor(name), definition=seed.capabilities.find(row=>row.id===id);
    for (const input_count of [0,1,33,2.5]) {
        assert.throws(()=>resolveGraphContract(definition.graph,{input_count})); assertions++; graphRejections++;
    }
    const parameters={input_count:3};
    const resolved=resolveGraphContract(definition.graph,parameters);
    const sample=resolved.inputs[0].type==='boolean'?true:resolved.inputs[0].cardinality==='many'?[]:1;
    const inputs=Object.fromEntries(resolved.inputs.map(p=>[p.id,sample]));
    delete inputs[resolved.inputs[2].id];
    assert.throws(()=>preview(id,parameters,inputs),/Missing runtime field/); assertions++; graphRejections++;
}
const sequence=seed.capabilities.find(row=>row.id==='forge.control.flow.sequence');
check(sequence?.version==='1.1.0','exact sequence metadata version');
check(resolveGraphContract(sequence.graph,{step_count:32}).outputs.length===32,'sequence authoring ports resolve to 32, not runtime execution');
write('variadic-reference.json',{kind:'test-only-variadic-values',gameVerified:false,assertions,graphRejections,cases});
console.log(JSON.stringify({status:'passed',cases:cases.length,assertions,graphRejections}));
