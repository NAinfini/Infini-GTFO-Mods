import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import assert from 'node:assert/strict';
const args=process.argv.slice(2);
if(args.length!==2)throw Error('Usage: recipient-filter-vectors.mjs <website> <output>');
const [site,output]=args.map(value=>path.resolve(value));
process.chdir(site);
await import(pathToFileURL(path.join(site,'Tools/register-typescript.ts')).href);
const {selectEffectRecipients}=await import(pathToFileURL(path.join(site,'site/forge/targeting.ts')).href);
const {previewLogicPrimitive}=await import(pathToFileURL(path.join(site,'site/forge/logic-evaluator.ts')).href);
const {logicPrimitiveDefinitions}=await import(pathToFileURL(path.join(site,'site/forge/logic-primitives.ts')).href);
const canonicalId='forge.selector.target.filter';
const definition=logicPrimitiveDefinitions.find(row=>row.id===canonicalId);
assert.equal(definition?.graph.execution,'pure');
const ref=id=>({id:'test.recipient:'+id,worldEpoch:1,lifeEpoch:1});
const entity=(id,kind,faction,position,lifeState='alive',tags=[],receives=['test.receiver.health'])=>
    ({ref:ref(id),kind,faction,position,lifeState,tags,receives});
const entities=[
    entity('owner','enemy','blue',[0,0,0]),entity('source','deployable','green',[10,0,0]),
    entity('instigator','player','red',[-10,0,0]),entity('self','deployable','blue',[0,0,0]),
    entity('b-enemy','enemy','blue',[-1,0,0],'alive',['veteran']),
    entity('b-player','player','blue',[1,0,0],'alive',['marked','veteran']),
    entity('r-enemy','enemy','red',[0,0,2],'alive',['marked']),entity('r-player','player','red',[0,0,-2]),
    entity('neutral','deployable','grey',[3,0,0]),entity('unknown','player',null,[0,0,0]),
    entity('downed','player','blue',[0,0,0],'downed'),entity('dead','enemy','red',[0,0,0],'dead'),
    entity('no-health','enemy','red',[2,0,0],'alive',[],['test.receiver.energy'])
];
const world={worldEpoch:1,entities,actors:{owner:ref('owner'),source:ref('source'),self:ref('self'),
    instigator:ref('instigator'),'event-target':ref('r-player')},relations:[
    {from:'blue',to:'blue',relation:'ally'},{from:'blue',to:'red',relation:'hostile'},
    {from:'blue',to:'grey',relation:'neutral'},{from:'red',to:'blue',relation:'ally'},
    {from:'red',to:'red',relation:'ally'},{from:'green',to:'blue',relation:'hostile'},
    {from:'green',to:'red',relation:'neutral'}]};
const allRelations=['self','ally','hostile','neutral','unknown'];
const base={schemaVersion:1,anchor:'owner',kinds:'any',relations:allRelations,
    lifeStates:['alive','downed','dead'],requireTags:[],excludeTags:[],sort:'stable-id',maxTargets:256};
const all=entities.map(row=>row.ref),cases=[];let assertions=2;
function add(name,overrides={},candidates=all,receivers=['test.receiver.health'],golden){
    const policy={...structuredClone(base),...overrides};
    const before=JSON.stringify({candidates,policy,world,receivers});
    const expected=selectEffectRecipients(candidates,policy,world,receivers);
    assert.notEqual(expected.status,'rejected');assertions++;
    assert.ok(!expected.rejected.some(row=>['stale-life','stale-world','entity-missing','target-limit'].includes(row.code)));assertions++;
    if(golden!==undefined){assert.deepEqual(expected.selected,golden.map(ref));assertions++;}
    assert.deepEqual(selectEffectRecipients(candidates,policy,world,receivers),expected);assertions++;
    assert.equal(JSON.stringify({candidates,policy,world,receivers}),before);assertions++;
    cases.push({id:cases.length+'-'+name,policy,candidates,receivers,outcome:'value',expected});
}
for(const anchor of Object.keys(world.actors))
    for(const relation of [...allRelations,'all'])
        for(const sort of ['stable-id','nearest','farthest'])
            add('roles-relations-order',{anchor,relations:relation==='all'?allRelations:[relation],sort});
for(const kinds of [['player'],['enemy'],['deployable'],['unknown-kind']])add('kind',{kinds});
for(const lifeState of ['alive','downed','dead'])add('life',{lifeStates:[lifeState]});
for(const tags of [['marked'],['marked','veteran'],['absent']])add('tags',{requireTags:tags});
add('exclude-tags',{excludeTags:['veteran','marked']});add('energy',{},all,['test.receiver.energy']);
add('no-receiver-requirement',{},all,[]);add('empty',{},[],[],[]);
const combat=['b-player','r-player','b-enemy','r-enemy'].map(ref);
for(const effect of ['damage','heal']){
    add(effect+'-allies',{relations:['ally']},combat,['test.receiver.health'],['b-enemy','b-player']);
    add(effect+'-hostile',{relations:['hostile']},combat,['test.receiver.health'],['r-enemy','r-player']);
}
add('exact-self',{relations:['self']},all,[],['owner']);
add('neutral',{relations:['neutral']},all,[],['neutral']);
add('unknown-is-explicit',{relations:['unknown']},all,[],['source','unknown']);
add('two-required-tags',{requireTags:['marked','veteran']},all,[],['b-player']);
for(let offset=0;offset<all.length;offset++){
    const rotated=[...all.slice(offset),...all.slice(0,offset),all[offset]];
    for(const sort of ['stable-id','nearest','farthest'])add('duplicates-permutation',{sort},rotated);
}
const capped={...base,maxTargets:1};
assert.throws(()=>previewLogicPrimitive(canonicalId,{empty:'emit-empty'},{candidates:combat,filter:capped},world),/truncated/);assertions++;
cases.push({id:'limit-rejection',policy:capped,candidates:combat,receivers:[],outcome:'rejected',expectedCode:'recipient-target-limit'});
for(const change of [{schemaVersion:2},{maxTargets:0},{maxTargets:257},{maxTargets:1.5},{kinds:[]},
    {relations:['friend']},{relations:['ally','ally']},{lifeStates:['sleeping']},{anchor:'attacker'},
    {sort:'random'},{requireTags:['marked'],excludeTags:['marked']},{execute:true}]){
    const policy={...base,...change};
    assert.throws(()=>selectEffectRecipients(combat,policy,world,[]));assertions++;
    cases.push({id:'policy-'+cases.length,policy,candidates:combat,receivers:[],outcome:'rejected',expectedCode:'recipient-policy'});
}
const report={kind:'test-only-recipient-filter-vectors',gameVerified:false,canonicalId,capabilityVersion:definition.version,
    assertions,world,cases};
fs.writeFileSync(path.join(output,'recipient-filter-reference.json'),JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify({status:'passed',assertions,cases:cases.length,canonicalId}));
