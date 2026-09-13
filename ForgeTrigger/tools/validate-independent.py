"""Run current variadic/weighted code and canonical audits; new mutations remain unavailable."""
from __future__ import annotations
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
MUTATIONS = [
    ('variadic-tail', 'Pure/VariadicNodes.cs', 'foreach (var value in values)\n            result =', 'foreach (var value in values[..^1])\n            result =', 'variadic'),
    ('variadic-all-tail', 'Pure/VariadicNodes.cs', 'foreach (var value in values) if (!value) return false;', 'foreach (var value in values[..^1]) if (!value) return false;', 'variadic'),
    ('variadic-union-pair', 'Pure/VariadicNodes.cs', 'i < snapshots.Length; i++) result = ReferenceCollections.Union', 'i < 2; i++) result = ReferenceCollections.Union', 'variadic'),
    ('weighted-ignore-mass', 'Pure/WeightedSampling.cs', 'var mass = Mass(candidate.Weight);', 'var mass = BigInteger.One;', 'weighted'),
    ('weighted-always-replace', 'Pure/WeightedSampling.cs', 'if (mode == WeightedSamplingMode.WithoutReplacement)', 'if (false)', 'weighted'),
    ('weighted-ignore-budget', 'Pure/WeightedSampling.cs', 'if (++used > budget)', 'if (++used > MaximumEntropyWords)', 'weighted'),
]

def sources(site: Path) -> list[Path]:
    paths = [ROOT/'ForgeTrigger.csproj', ROOT/'ModuleDefinition.cs', Path(__file__).resolve(),
             ROOT/'tests/Contracts/AuthoringContractTests.cs', ROOT/'tests/Contracts/GraphResolutionChecks.cs', ROOT/'tools/graph-metadata-vectors.mjs']
    for folder in ['Pure', 'Targeting', 'tests/Acceptance']:
        paths += [p for p in (ROOT/folder).glob('*') if p.suffix in {'.cs', '.csproj'}]
    paths += [ROOT/'tools'/name for name in ['acceptance-vectors.mjs', 'weighted-vectors.mjs', 'authoring-contract-audit.mjs']]
    paths += list((ROOT.parent/'ForgeRuntime/Framework').glob('*.cs'))
    paths += [ROOT.parent/'ForgeRuntime/Framework/ForgeRuntime.Framework.csproj']
    paths += list((site/'site/forge').rglob('*.ts'))
    paths += [site/'Tools/register-typescript.ts', site/'pnpm-lock.yaml', site/'node_modules/typescript/package.json']
    return sorted(set(paths))

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--site', type=Path, default=ROOT.parents[1]/'Infini-GTFO-Model-Site')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--mutations', action='store_true')
    args = parser.parse_args(); site = args.site.resolve()
    output = (args.output or ROOT/'artifacts'/('independent-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))).resolve()
    if not output.is_relative_to((ROOT/'artifacts').resolve()):
        raise ValueError('Output must be a fresh directory inside ForgeTrigger/artifacts.')
    output.mkdir(parents=True, exist_ok=False)
    hashes = lambda: {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources(site)}
    read = lambda p: json.loads(p.read_text(encoding='utf-8'))
    report = {'status':'failed', 'requestedScope':'independent-computations', 'gameVerified':False,
              'publicationReady':False, 'commands':[], 'mutations':[]}
    def run(name: str, argv: list[str], allow_failure: bool = False) -> int:
        log = output/(name+'.log'); print('RUN '+name, flush=True)
        command = {'name':name, 'argv':argv, 'log':log.name}
        report['commands'].append(command)
        try:
            with log.open('w', encoding='utf-8') as stream:
                result = subprocess.run(argv, cwd=ROOT.parent, stdout=stream, stderr=subprocess.STDOUT, timeout=240)
            command['exitCode'] = result.returncode
        except subprocess.TimeoutExpired:
            command['status'] = 'timeout'; raise
        print(log.read_text(encoding='utf-8', errors='replace')[-1600:], flush=True)
        if result.returncode and not allow_failure:
            raise RuntimeError(name+' failed with exit '+str(result.returncode))
        return result.returncode
    def build(name: str, project: Path, destination: Path) -> Path:
        run(name, ['dotnet','build',str(project),'-c','Release','--artifacts-path',str(destination),'--disable-build-servers'])
        dlls = [p for p in (destination/'bin').rglob('ForgeTrigger.AcceptanceTests.dll') if p.with_suffix('.runtimeconfig.json').exists()]
        if len(dlls) != 1:
            raise RuntimeError('Expected exactly one runnable acceptance assembly.')
        return dlls[0]
    def groups_executed(value: dict) -> bool:
        return sorted(value.get('groups', [])) == ['VariadicTests','WeightedTests']
    try:
        before = hashes(); report['sourcesBefore'] = before
        dll = build('build', ROOT/'tests/Acceptance/Acceptance.csproj', output/'build')
        run('export', ['dotnet',str(dll),'export',str(output)])
        run('authoring-vectors', ['node',str(ROOT/'tools/acceptance-vectors.mjs'),str(site),str(output)])
        run('weighted-vectors', ['node',str(ROOT/'tools/weighted-vectors.mjs'),str(output)])
        run('behavior', ['dotnet',str(dll),'check',str(output)])
        result = read(output/'acceptance-result.json')
        if result['status'] != 'passed' or not groups_executed(result):
            raise RuntimeError('Both required behavior groups must execute and pass.')
        report['behavior'] = result
        report['authoringCompatibility'] = read(output/'authoring-compatibility-result.json')
        report['runtimeReady'] = report['authoringCompatibility']['runtimeReady']
        report['variadicCases'] = len(read(output/'variadic-reference.json')['cases'])
        report['weightedCases'] = len(read(output/'weighted-reference.json')['cases'])
        fixture_files = [output/name for name in ['authoring-registration-cases.json','variadic-reference.json','weighted-reference.json']]
        report['fixtureHashes'] = {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in fixture_files}
        after = hashes(); report['sourcesAfter'] = after
        if after != before:
            raise RuntimeError('Consumed source changed during the independent validation.')
        report['checksStatus'] = 'passed'
        report['mutations'] = {'status':'not-executed', 'cases':[row[0] for row in MUTATIONS],
                               'reason':'New mutation automation was not delivered; baseline tests are not mutation evidence.'}
        if args.mutations:
            report['status'] = 'blocked'
            print('BASELINE PASS; requested new mutation coverage is unavailable.', flush=True)
            return 2
        report['status'] = 'passed'
        print('PASS independent baseline; this is not a runtime-support certificate.', flush=True)
        return 0
    except Exception as error:
        report['error'] = str(error)
        print('FAIL: '+str(error), file=sys.stderr, flush=True)
        return 1
    finally:
        try: report['sourcesAfter'] = hashes()
        except Exception as error: report['sourceReadError'] = str(error)
        (output/'summary.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')

if __name__ == '__main__':
    raise SystemExit(main())
