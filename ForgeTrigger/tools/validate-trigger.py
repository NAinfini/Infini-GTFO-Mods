"""Full Trigger gate: pure computations, public R3 integration and T1; failures stay separate."""
from __future__ import annotations
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
R3_MUTATIONS = [('actor-fallback',
  'Targeting/ObservedEntityNodes.cs',
  'runtime.InspectActor(actors, role)',
  'runtime.InspectActor(actors, role == "owner" && actors.Get("owner") == null ? "source" : role)'),
 ('receiver-ignored',
  'Targeting/ObservedEntityNodes.cs',
  'if (!snapshot.Receives.Contains(capability, StringComparer.Ordinal))',
  'if (false)'),
 ('relation-reversed',
  'Targeting/ObservedEntityNodes.cs',
  'relations.Resolve(snapshots.Single(row => row.Ref == anchor), snapshots.Single(row => row.Ref == '
  'recipient))',
  'relations.Resolve(snapshots.Single(row => row.Ref == recipient), snapshots.Single(row => row.Ref == '
  'anchor))'),
 ('exists-unknown-false',
  'Targeting/ObservedEntityNodes.cs',
  'if (query.IsComplete) return true;',
  'if (!query.IsComplete) return false;\n        if (query.IsComplete) return true;'),
 ('sphere-exclusive',
  'Targeting/ObservedSpatialNodes.cs',
  'Distance(row.Position, point) <= radius',
  'Distance(row.Position, point) < radius'),
 ('chain-revisits',
  'Targeting/ObservedSpatialNodes.cs',
  'selected.Add(next.Row.Ref); remaining.Remove(next.Row); from = next.Row.Position;',
  'selected.Add(next.Row.Ref); from = next.Row.Position;'),
 ('nearest-unstable-ties',
  'Targeting/ObservedSpatialNodes.cs',
  'ordered.ThenBy(row => ReferenceCollections.OrderKey(row.Row.Ref), StringComparer.Ordinal)',
  'ordered.ThenBy(row => "", StringComparer.Ordinal)')]

R3_MUTATIONS += [
  ('filter-reversed-relation', 'Targeting/ObservedRecipientFilter.cs',
   'relations.Resolve(anchorSnapshot, row)', 'relations.Resolve(row, anchorSnapshot)'),
  ('filter-anchor-replaced', 'Targeting/ObservedRecipientFilter.cs',
   'observed.Single(row => row.Ref == anchor)', 'observed.Single(row => row.Ref == captured[0])'),
  ('filter-keeps-anchor', 'Targeting/ObservedRecipientFilter.cs',
   'var candidatesSet = new HashSet<EntityReference>(captured);',
   'var candidatesSet = new HashSet<EntityReference>(requested);'),
  ('filter-unordered', 'Targeting/ObservedRecipientFilter.cs',
   '.OrderBy(row => ReferenceCollections.OrderKey(row), StringComparer.Ordinal)',
   '.OrderBy(row => string.Empty, StringComparer.Ordinal)'),
  ('filter-cardinality-lies', 'Targeting/ObservedRecipientFilter.cs',
   'new RecipientFilterSelection(matched, captured.Length, candidatesSet.Count)',
   'new RecipientFilterSelection(matched, candidatesSet.Count, candidatesSet.Count)')]

R3_MUTATIONS += [('capsule-exclusive', 'Targeting/ObservedSpatialNodes.cs', 'point[2])) <= radius,', 'point[2])) < radius,'), ('box-shallow', 'Targeting/ObservedSpatialNodes.cs', 'Delta(row.Position[1], point[1]) <= height / 2d && Delta(row.Position[2], point[2]) <= radius', 'Delta(row.Position[1], point[1]) <= radius && Delta(row.Position[2], point[2]) <= radius')]

def sources(site: Path) -> list[Path]:
    paths = [ROOT/'ModuleDefinition.cs', ROOT/'ForgeTrigger.csproj']
    for directory in ['Pure','Targeting','tests/Pure','tests/R3Consumers','tests/Contracts','tests/Acceptance','tools']:
        paths += [p for p in (ROOT/directory).glob('*') if p.suffix in {'.cs','.csproj','.py','.mjs'}]
    paths += list((ROOT/'tests/fixtures').rglob('*.json'))
    paths += list((ROOT.parent/'ForgeRuntime/Framework').rglob('*.cs'))
    paths += [ROOT.parent/'ForgeRuntime/Framework/ForgeRuntime.Framework.csproj']
    paths += list((site/'site/forge').rglob('*.ts'))
    paths += [site/'Tools/register-typescript.ts',site/'catalog/capability-catalog.json']
    return sorted(set(paths))
def main() -> int:
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--site',type=Path,default=ROOT.parents[1]/'Infini-GTFO-Model-Site')
    parser.add_argument('--output',type=Path)
    parser.add_argument('--mutations',action='store_true')
    parser.add_argument('--r3-only',action='store_true',help='Run only public R3/spatial/filter consumers; this is not the full Trigger gate.')
    args=parser.parse_args();site=args.site.resolve()
    output=(args.output or ROOT/'artifacts'/('trigger-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))).resolve()
    if not output.is_relative_to((ROOT/'artifacts').resolve()): raise ValueError('Output must stay in Trigger artifacts.')
    output.mkdir(parents=True,exist_ok=False)
    hashes=lambda:{str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in sources(site)}
    report={'status':'failed','requestedScope':'r3-only' if args.r3_only else 'full','gameVerified':False,'commands':[],'sourcesBefore':hashes(),'scopes':{}}
    def run(name: str, argv: list[str]) -> int:
        log=output/(name+'.log');print('RUN '+name,flush=True)
        with log.open('w',encoding='utf-8') as stream:
            result=subprocess.run(argv,cwd=ROOT.parent,stdout=stream,stderr=subprocess.STDOUT,timeout=240)
        report['commands'].append({'name':name,'argv':argv,'exitCode':result.returncode,'log':log.name})
        print(log.read_text(encoding='utf-8',errors='replace')[-1200:],flush=True)
        return result.returncode
    def summary(path: Path) -> dict:
        return json.loads(path.read_text(encoding='utf-8')) if path.exists() else {'status':'failed','error':'Result file missing'}
    def r3build(name: str, project: Path, destination: Path) -> Path:
        if run(name,['dotnet','build',str(project),'-c','Release','--artifacts-path',str(destination),'--disable-build-servers']):
            raise RuntimeError(name+' failed to compile; this is not a mutation detection.')
        dlls=[p for p in (destination/'bin').rglob('ForgeTrigger.R3ConsumerTests.dll') if p.with_suffix('.runtimeconfig.json').exists()]
        if len(dlls)!=1: raise RuntimeError('Missing/ambiguous R3 test assembly')
        return dlls[0]
    try:
        if not args.r3_only:
            pure_args=[sys.executable,'-X','utf8','-u',str(ROOT/'tools/validate-pure.py'),'--site',str(site),'--output',str(output/'pure')]
            if args.mutations: pure_args.append('--mutations')
            pure_code=run('pure',pure_args)
            report['scopes']['pure']=summary(output/'pure/summary.json')
            report['scopes']['pure']['exitCode']=pure_code
            t1_code=run('t1',[sys.executable,'-X','utf8','-u',str(ROOT/'tools/validate-t1.py'),'--site',str(site),'--output',str(output/'t1')])
            report['scopes']['t1']=summary(output/'t1/summary.json')
            report['scopes']['t1']['exitCode']=t1_code
            independent_args=[sys.executable,'-X','utf8','-u',str(ROOT/'tools/validate-independent.py'),'--site',str(site),'--output',str(output/'independent')]
            if args.mutations: independent_args.append('--mutations')
            independent_code=run('independent',independent_args)
            report['scopes']['independent']=summary(output/'independent/summary.json')
            report['scopes']['independent']['exitCode']=independent_code
        if run('spatial-fixtures',['node',str(ROOT/'tools/spatial-vectors.mjs'),str(site),str(output)]):
            raise RuntimeError('Spatial fixture generation failed.')
        if run('recipient-filter-fixtures',['node',str(ROOT/'tools/recipient-filter-vectors.mjs'),str(site),str(output)]):
            raise RuntimeError('Recipient filter fixture generation failed.')
        dll=r3build('r3-build',ROOT/'tests/R3Consumers/R3Consumers.csproj',output/'r3-build')
        r3_code=run('r3',['dotnet',str(dll),str(output/'r3-result.json'),str(output/'spatial-reference.json'),str(output/'recipient-filter-reference.json')])
        report['scopes']['r3']=summary(output/'r3-result.json')
        report['scopes']['r3']['exitCode']=r3_code
        if args.mutations and r3_code:
            report['r3Mutations']={'status':'blocked-by-failed-r3-baseline','cases':[row[0] for row in R3_MUTATIONS]}
        elif args.mutations:
            mutation_results=[]
            copy_sources=[p for p in sources(site) if p.suffix in {'.cs','.csproj'} and p.is_relative_to(ROOT.parent)]
            for name,relative,old,new in [('clean-r3',None,None,None)]+R3_MUTATIONS:
                workspace=output/'r3-mutations'/name;workspace.mkdir(parents=True,exist_ok=False)
                for source in copy_sources:
                    data=source.read_bytes()
                    if hashlib.sha256(data).hexdigest()!=report['sourcesBefore'][str(source)]:
                        raise RuntimeError('Source drift before R3 mutation snapshot')
                    target=workspace/source.relative_to(ROOT.parent);target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
                if old is not None:
                    target=workspace/'ForgeTrigger'/relative
                    text=target.read_text(encoding='utf-8-sig')
                    if text.count(old)!=1: raise RuntimeError('R3 mutation anchor is not unique: '+name)
                    target.write_text(text.replace(old,new),encoding='utf-8')
                mutated=r3build(name+'-build',workspace/'ForgeTrigger/tests/R3Consumers/R3Consumers.csproj',workspace/'build')
                code=run(name,['dotnet',str(mutated),str(workspace/'result.json'),str(output/'spatial-reference.json'),str(output/'recipient-filter-reference.json')])
                result=summary(workspace/'result.json')
                expected=(code==0 and result['status']=='passed') if old is None else (code==1 and result['status']=='failed' and len(result['failures'])>0)
                mutation_results.append({'name':name,'detected':expected,'exitCode':code,'result':result})
                if not expected: raise RuntimeError('R3 mutation was not detected: '+name)
            report['r3Mutations']={'status':'passed','cases':mutation_results}
        after=hashes();report['sourcesAfter']=after
        if after!=report['sourcesBefore']: raise RuntimeError('Consumed source changed during full validation.')
        expected={'r3'} if args.r3_only else {'pure','t1','independent','r3'}
        if set(report['scopes'])!=expected: raise RuntimeError('A required validation scope was not executed.')
        errors=[]; blocked=[]
        for name,scope in report['scopes'].items():
            if scope.get('status')=='passed' and scope.get('exitCode')==0: continue
            if name=='t1' and scope.get('status')=='blocked' and scope.get('exitCode')==2 and scope.get('checksStatus')=='passed':
                blocked.append(name)
            else: errors.append(name)
        report['checksStatus']='failed' if errors else 'passed'
        report['blockedScopes']=blocked
        report['publicationReady']=False; report['planComplete']=False
        report['status']='failed' if errors else 'blocked' if blocked else 'passed'
        print(json.dumps({'status':report['status'],'checksStatus':report['checksStatus'],'requestedScope':report['requestedScope'],'scopes':{k:v.get('status') for k,v in report['scopes'].items()}}),flush=True)
        return 1 if errors else 2 if blocked else 0
    except Exception as error:
        report['error']=str(error);print('FAIL: '+str(error),file=sys.stderr,flush=True);return 1
    finally:
        (output/'summary.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')

if __name__=='__main__':
    raise SystemExit(main())
