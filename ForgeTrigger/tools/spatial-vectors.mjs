import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import assert from 'node:assert/strict';
const args=process.argv.slice(2);
if(args.length!==2) throw Error('Usage: spatial-vectors.mjs <website> <output>');
const [site,output]=args.map(value=>path.resolve(value));
process.chdir(site);
await import(pathToFileURL(path.join(site,'Tools/register-typescript.ts')).href);
const {previewLogicPrimitive:preview}=await import(pathToFileURL(path.join(site,'site/forge/logic-evaluator.ts')).href);
const {logicPrimitiveDefinitions}=await import(pathToFileURL(path.join(site,'site/forge/logic-primitives.ts')).href);
const ref=id=>({id:'test.spatial:'+id,worldEpoch:1,lifeEpoch:1});
const positions={origin:[0,0,0],a:[1,0,0],b:[-1,0,0],c:[3,0,0],d:[5,0,0],v:[0,3,0],diagonal:[3,4,0]};
const world={worldEpoch:1,relations:[],actors:{source:ref('origin')},entities:Object.entries(positions).map(([id,position])=>({
    ref:ref(id),kind:'enemy',faction:null,lifeState:'alive',tags:[],receives:[],position}))};
const names=['shape_overlap','nearest','farthest','chain'];
const versions=Object.fromEntries(names.map(name=>{
    const definition=logicPrimitiveDefinitions.find(row=>row.id==='forge.selector.target.'+name);
    assert.equal(definition?.graph.execution,'pure');
    return [definition.id,definition.version];
}));
const emit={empty:'emit-empty'};
const rows=[],unimplemented=[];let assertions=0;
function evaluate(name,parameters,inputs,golden){
    const before=JSON.stringify({world,inputs,parameters});
    const result=preview('forge.selector.target.'+name,parameters,inputs,world);
    assert.equal(result.kind,'preview-only');assertions++;
    assert.deepEqual(preview('forge.selector.target.'+name,parameters,inputs,world).outputs,result.outputs);assertions++;
    assert.equal(JSON.stringify({world,inputs,parameters}),before);assertions++;
    if(golden!==undefined){assert.deepEqual(result.outputs.targets,golden);assertions++;}
    return {name,parameters,inputs,expected:result.outputs.targets};
}
const add=(...row)=>rows.push(evaluate(...row));
// shape_overlap queries the whole world sample; it has no candidate input.
const volume=(shape,center,radius,height)=>[{shape,...emit},{center,radius,angle:0,height,extents:[radius,height/2,radius]}];
for(const shape of ['sphere','cylinder']) for(const center of [[0,0,0],[3,0,0]]) for(const radius of [1,3,5]) for(const height of [2,8])
    add('shape_overlap',...volume(shape,center,radius,height));
// The C# observed volume implements point sphere/cylinder only; these website results are recorded as a gap, not consumed.
for(const shape of ['capsule','box'])
    unimplemented.push({...evaluate('shape_overlap',...volume(shape,[0,0,0],3,8)),reason:'ObservedVolumeShape has no '+shape});
const all=Object.keys(positions).filter(id=>id!=='origin').map(ref);
for(const candidates of [[],[ref('a')],all,[...all].reverse(),[...all,...all]]){
    for(const anchor of [ref('origin'),ref('a')]){
        for(const max_targets of [1,2,8]) for(const name of ['nearest','farthest'])
            add(name,emit,{anchor,candidates,max_targets});
        for(const radius of [1,2,4])
            add('chain',emit,{origin:anchor,candidates,hops:4,radius});
    }
}
add('nearest',emit,{anchor:ref('origin'),candidates:[ref('b'),ref('a')],max_targets:2},[ref('a'),ref('b')]);
add('chain',emit,{origin:ref('origin'),candidates:['a','b','c','d'].map(ref),hops:4,radius:2},['a','b'].map(ref));
add('shape_overlap',...volume('sphere',[3,4,0],0.5,2),[ref('diagonal')]);
const report={kind:'test-only-spatial-vectors',gameVerified:false,assertions,versions,world,caseCount:rows.length,cases:rows,unimplemented};
fs.writeFileSync(path.join(output,'spatial-reference.json'),JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify({status:'passed',assertions,cases:rows.length,unimplemented:unimplemented.length}));
