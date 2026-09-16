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
const {logicPrimitiveDefinitions}=await import(pathToFileURL(path.join(site,'site/forge/logic-primitives.ts')).href);
const canonicalId='forge.selector.target.filter';
const definition=logicPrimitiveDefinitions.find(row=>row.id===canonicalId);
assert.equal(definition?.graph.execution,'query');
const ref=id=>({id:'test.recipient:'+id,worldEpoch:1,lifeEpoch:1});
const entity=(id,kind,faction,position,lifeState='alive',tags=[],receives=['test.receiver.health'])=>
    ({ref:ref(id),kind,faction,position,lifeState,tags,receives});
// The anchor of a relation is the row's own `anchor` input, and every case names which context actor carries it:
// the input is a reference, so the same candidates measured against `owner` must answer a different set than the
// same candidates measured against `self`. The other actors are present so a case that reached for one of them
// instead would answer a different set rather than fail loudly.
const entities=[
    entity('self','deployable','blue',[0,0,0]),
    entity('owner','enemy','green',[10,0,0]),entity('source','deployable','grey',[20,0,0]),
    entity('instigator','player','red',[-10,0,0]),entity('event-target','player','red',[0,0,-5]),
    entity('ally-enemy','enemy','blue',[-1,0,0],'alive',['veteran']),
    entity('ally-player','player','blue',[1,0,0],'alive',['marked','veteran']),
    entity('hostile-enemy','enemy','red',[0,0,2],'alive',['marked']),
    entity('hostile-player','player','red',[0,0,-2]),
    entity('neutral','deployable','grey',[3,0,0]),
    entity('unknown','player',null,[0,0,0]),
    entity('no-health','enemy','red',[2,0,0],'alive',[],['test.receiver.energy'])
];
const actors={self:ref('self'),owner:ref('owner'),source:ref('source'),
    instigator:ref('instigator'),'event-target':ref('event-target')};
const world={worldEpoch:1,entities,actors,relations:[
    {from:'blue',to:'blue',relation:'ally'},{from:'blue',to:'red',relation:'hostile'},
    {from:'blue',to:'grey',relation:'neutral'},{from:'red',to:'blue',relation:'ally'},
    {from:'red',to:'red',relation:'ally'},{from:'green',to:'blue',relation:'hostile'},
    {from:'green',to:'red',relation:'neutral'},{from:'grey',to:'blue',relation:'neutral'}]};
const allRelations=['self','ally','hostile','neutral','unknown'];
const anchorRoles=['self','owner'];
const all=entities.map(row=>row.ref),cases=[],notes=[];let assertions=3;
// The crowd a budget case needs is part of the world itself: every reference a case names must be observable, and
// the normal cases name their own candidates explicitly, so extra allies change none of their answers.
const crowdIds=Array.from({length:256-all.length},(_,index)=>'crowd-'+index);
for(const id of crowdIds)entities.push(entity(id,'enemy','blue',[100+Number(id.slice(6)),0,0]));
const crowd=[...all,...crowdIds.map(ref)];
// The catalog declares anchor, relation and empty for this row and nothing else, so the expectation is the site's
// own selection narrowed to the one relation and anchored at the actor the case wires into `anchor`: every other
// policy field is held at the value the site uses for a relation-only filter, and the reference population carries
// no downed or dead entity, so the row's shape is the whole difference between the two answers.
function expectedFor(relation,candidates,anchorRole){
    const policy={schemaVersion:1,anchor:anchorRole,kinds:'any',relations:[relation],lifeStates:['alive'],
        requireTags:[],excludeTags:[],sort:'stable-id',maxTargets:256};
    return selectEffectRecipients([...candidates],policy,world,[]);
}
function add(name,relation,candidates=all,anchorRole='self',golden){
    const selection=expectedFor(relation,candidates,anchorRole);
    assert.notEqual(selection.status,'rejected');assertions++;
    assert.ok(!selection.rejected.some(row=>['stale-life','stale-world','entity-missing','target-limit'].includes(row.code)));assertions++;
    if(golden!==undefined){assert.deepEqual(selection.selected,[...golden].map(ref));assertions++;}
    assert.deepEqual(expectedFor(relation,candidates,anchorRole).selected,selection.selected);assertions++;
    cases.push({id:cases.length+'-'+name,relation,anchorRole,anchor:actors[anchorRole],candidates,expected:selection.selected});
}
for(const relation of allRelations)add('relation-'+relation,relation);
add('relation-hostile-shuffled',   'hostile',[...all].reverse());
add('relation-hostile-duplicated', 'hostile',[...all,...all]);
add('relation-ally-subset',        'ally',['ally-enemy','ally-player','self'].map(ref));
add('relation-hostile-empty',      'hostile',[]);
// A non-self anchor is the whole point of the explicit port: the green owner has no declared rule towards blue or
// red, so the same candidates answer `unknown` where the blue self anchor answered `ally` or `hostile`.
add('anchor-owner-unknown',        'unknown',all,'owner');
add('anchor-owner-hostile',        'hostile',all,'owner');
add('anchor-owner-self',           'self',all,'owner');
add('anchor-owner-ally',           'ally',all,'owner');
// A relation the world says nothing about is `unknown`, which is a relation like any other: it is answered only
// when it was asked for, never folded into "no match" or into neutrality.
assert.deepEqual(expectedFor('unknown',all,'self').selected,[ref('owner'),ref('unknown')]);assertions++;
assert.deepEqual(expectedFor('neutral',all,'self').selected,[ref('neutral'),ref('source')]);assertions++;
// The selection budget is 256 targets, and the C# read is bounded by the same 256 references per query, so the
// largest candidate set both sides can answer is a crowd of exactly that many: they must agree on which survive.
assert.equal(crowd.length,256);assertions++;
const crowdSelection=selectEffectRecipients(crowd,{schemaVersion:1,anchor:'self',kinds:'any',relations:['ally'],
    lifeStates:['alive'],requireTags:[],excludeTags:[],sort:'stable-id',maxTargets:256},world,[]);
assert.equal(crowdSelection.status,'selected');assertions++;
assert.equal(crowdSelection.selected.length,246);assertions++;
assert.equal(crowdSelection.rejected.length,10);assertions++;
cases.push({id:cases.length+'-relation-ally-crowd',relation:'ally',anchorRole:'self',anchor:actors.self,candidates:crowd,expected:crowdSelection.selected});
const report={kind:'test-only-recipient-filter-vectors',gameVerified:false,canonicalId,capabilityVersion:definition.version,
    anchorRoles,relations:allRelations,assertions,world,cases,notes};
fs.writeFileSync(path.join(output,'recipient-filter-reference.json'),JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify({status:'passed',assertions,cases:cases.length,canonicalId}));
