import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import assert from 'node:assert/strict';
const args=process.argv.slice(2);
if(args.length!==2) throw Error('Usage: spatial-vectors.mjs <website> <output>');
const [site,output]=args.map(value=>path.resolve(value));
process.chdir(site);
await import(pathToFileURL(path.join(site,'Tools/register-typescript.ts')).href);
const {previewLogicPrimitive:preview}=await import(pathToFileURL(path.join(site,'site/forge/logic-preview.ts')).href);
const ref=id=>({id:'test.spatial:'+id,worldEpoch:1,lifeEpoch:1});
const positions={origin:[0,0,0],a:[1,0,0],b:[-1,0,0],c:[3,0,0],d:[5,0,0],v:[0,3,0],diagonal:[3,4,0]};
const world={worldEpoch:1,relations:[],actors:{source:ref('origin')},entities:Object.entries(positions).map(([id,position])=>({
    ref:ref(id),kind:'enemy',faction:null,lifeState:'alive',tags:[],receives:[],position}))};
const rows=[];let assertions=0;
function add(name,parameters,inputs,golden){
    const before=JSON.stringify({world,inputs,parameters});
    const result=preview('forge.selector.target.'+name,parameters,inputs,world);
    assert.equal(result.kind,'preview-only');assertions++;
    assert.deepEqual(preview('forge.selector.target.'+name,parameters,inputs,world).outputs,result.outputs);assertions++;
    assert.equal(JSON.stringify({world,inputs,parameters}),before);assertions++;
    if(golden!==undefined){assert.deepEqual(result.outputs.targets,golden);assertions++;}
    rows.push({name,parameters,inputs,expected:result.outputs.targets});
}
const all=Object.keys(positions).filter(id=>id!=='origin').map(ref);
for(const targets of [[],[ref('a')],all,[...all].reverse(),[...all,...all]]){
    for(const shape of ['sphere','cylinder']) for(const radius of [1,3,5])
        add('shape_overlap',{shape,radius,height:2},{targets,center:[0,0,0],complete:true});
    for(const count of [1,2,8]) for(const name of ['nearest','farthest'])
        add(name,{count},{targets,center:[0,0,0]});
    for(const radius of [1,2,4])
        add('chain',{max_hops:4,radius},{targets,start:ref('origin')});
}
add('nearest',{count:2},{targets:[ref('b'),ref('a')],center:[0,0,0]},[ref('a'),ref('b')]);
add('chain',{max_hops:4,radius:2},{targets:['a','b','c','d'].map(ref),start:ref('origin')},['a','b'].map(ref));
add('shape_overlap',{shape:'sphere',radius:5,height:2},{targets:[ref('diagonal')],center:[0,0,0],complete:true},[ref('diagonal')]);
const report={kind:'test-only-spatial-vectors',gameVerified:false,assertions,world,cases:rows};
fs.writeFileSync(path.join(output,'spatial-reference.json'),JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify({status:'passed',assertions,cases:rows.length}));
