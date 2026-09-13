import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
if (process.argv.length!==3) throw Error('Usage: weighted-vectors.mjs <evidence directory>');
const output=path.resolve(process.argv[2]);
const algorithm='binary64-integer-mass-mulberry32-v1';
function mass(value) {
    const bytes=Buffer.alloc(8); bytes.writeDoubleBE(value);
    const bits=bytes.readBigUInt64BE(), exponent=Number((bits>>52n)&2047n), fraction=bits&((1n<<52n)-1n);
    return exponent===0?fraction:(fraction|(1n<<52n))<<BigInt(exponent-1);
}
function gcd(a,b){while(b!==0n){[a,b]=[b,a%b];}return a;}
const key=ref=>JSON.stringify([ref.worldEpoch,ref.id,ref.lifeEpoch]);
function sample(candidates,count,seed,mode) {
    const rows=candidates.filter(r=>r.weight>0).map(r=>({target:r.target,mass:mass(r.weight)}));
    rows.sort((a,b)=>key(a.target)<key(b.target)?-1:key(a.target)>key(b.target)?1:0);
    const divisor=rows.reduce((g,row)=>gcd(g,row.mass),0n);
    for(const row of rows)row.mass/=divisor;
    let total=rows.reduce((sum,r)=>sum+r.mass,0n),state=seed>>>0,entropyWords=0;
    const next=()=>{state=(state+0x6d2b79f5)>>>0;let v=Math.imul(state^(state>>>15),1|state);v^=v+Math.imul(v^(v>>>7),61|v);return(v^(v>>>14))>>>0;};
    const below=bound=>{
        if(bound===1n)return 0n;
        const bits=(bound-1n).toString(2).length;
        for(let attempt=0;attempt<64;attempt++) {
            let ticket=0n;
            for(let offset=0;offset<bits;offset+=32) {
                if(++entropyWords>65536)throw Error('reference entropy budget');
                const take=Math.min(32,bits-offset),mask=(1n<<BigInt(take))-1n;
                ticket|=(BigInt(next())&mask)<<BigInt(offset);
            }
            if(ticket<bound)return ticket;
        }
        throw Error('reference rejection budget');
    };
    const selected=[];
    while(selected.length<count&&total>0n) {
        let ticket=below(total);
        for(const row of rows) {
            if(ticket<row.mass) {
                selected.push(row.target);
                if(mode==='without-replacement'){total-=row.mass;row.mass=0n;}
                break;
            }
            ticket-=row.mass;
        }
    }
    return {selected,entropyWords,eligible:candidates.filter(r=>r.weight>0).length};
}
const cases=[];let assertions=0;
const sets=[[],[0],[1],[1,1,1],[0,1,3],[1e-300,2e-300,3e-300],
    [Number.MIN_VALUE,Number.MAX_VALUE],[1e300,1e300],[Number.MIN_VALUE,Number.MIN_VALUE,Number.MIN_VALUE],[0.25,0.5,0.75]];
for(const weights of sets)for(const seed of [0,1,7,42,4294967295])for(const count of [1,2,5])
    for(const mode of ['without-replacement','with-replacement']) {
        const candidates=weights.map((weight,i)=>({target:{id:'test.weight:'+i,worldEpoch:1,lifeEpoch:i%3+1},weight}));
        const expected=sample(candidates,count,seed,mode);
        assert.deepEqual(expected,sample([...candidates].reverse(),count,seed,mode)); assertions++;
        const selectedCount=expected.eligible===0?0:mode==='with-replacement'?count:Math.min(count,expected.eligible);
        assert.equal(expected.selected.length,selectedCount); assertions++;
        if(mode==='without-replacement'){assert.equal(new Set(expected.selected.map(key)).size,expected.selected.length);assertions++;}
        if(expected.eligible===1){assert.equal(expected.entropyWords,0);assertions++;}
        cases.push({candidates,count,seed,mode,expected});
    }
fs.writeFileSync(path.join(output,'weighted-reference.json'),JSON.stringify({kind:'test-only-weighted-algorithm-reference',
    algorithm,plannedCanonical:'forge.selector.target.weighted',runtimeBinding:null,gameVerified:false,assertions,cases},null,2)+'\n');
console.log(JSON.stringify({status:'passed',algorithm,cases:cases.length,assertions}));
