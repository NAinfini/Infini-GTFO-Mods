"""Run variadic/weighted computations and canonical audits; --mutations proves each seeded defect is detected."""
from __future__ import annotations
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
MUTATIONS = [
    ('variadic-tail', 'Pure/VariadicNodes.cs', 'foreach (var value in values)\n            result =', 'foreach (var value in values[..^1])\n            result =', 'variadic'),
    ('variadic-all-tail', 'Pure/VariadicNodes.cs', 'foreach (var value in values) if (!value) return false;', 'foreach (var value in values[..^1]) if (!value) return false;', 'variadic'),
    ('variadic-any-tail', 'Pure/VariadicNodes.cs', 'foreach (var value in values) if (value) return true;', 'foreach (var value in values[..^1]) if (value) return true;', 'variadic'),
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
    parser.add_argument('--out', '--output', dest='output', type=Path,
                        help='Evidence directory; inside ForgeTrigger/artifacts or the system temporary directory.')
    parser.add_argument('--mutations', action='store_true')
    args = parser.parse_args(); site = args.site.resolve()
    artifacts = (ROOT/'artifacts').resolve(); temporary = Path(tempfile.gettempdir()).resolve()
    output = (args.output or artifacts/('independent-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))).resolve()
    # Not a shared directory: this run owns its evidence directory, so an existing path is a previous run's
    # output (or a tracked file) and is never overwritten.
    if not (output.is_relative_to(artifacts) or output.is_relative_to(temporary)):
        raise ValueError('Output must stay inside ForgeTrigger/artifacts or the system temporary directory.')
    if output.exists():
        raise ValueError('Output must not already exist; every run writes its own directory: '+str(output))
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
        if args.mutations:
            # Each defect is built from a hash-checked copy and judged against the clean run's reference
            # vectors; a copy that fails to build aborts the gate instead of counting as detected.
            cases = []
            copy_sources = [p for p in sources(site) if p.suffix in {'.cs', '.csproj'} and p.is_relative_to(ROOT.parent)]
            evidence = ['authoring-registration-cases.json', 'variadic-reference.json', 'weighted-reference.json']
            for name, relative, old, new, group in [('clean-copy', None, None, None, None)] + MUTATIONS:
                workspace = output/'mutations'/name; workspace.mkdir(parents=True, exist_ok=False)
                for source in copy_sources:
                    data = source.read_bytes()
                    if hashlib.sha256(data).hexdigest() != before[str(source)]:
                        raise RuntimeError('Source drift before mutation snapshot.')
                    target = workspace/source.relative_to(ROOT.parent)
                    target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)
                if old is not None:
                    target = workspace/'ForgeTrigger'/relative
                    text = target.read_text(encoding='utf-8-sig')
                    if text.count(old) != 1: raise RuntimeError('Mutation anchor is not unique: '+name)
                    target.write_text(text.replace(old, new), encoding='utf-8')
                mutated = build(name+'-build', workspace/'ForgeTrigger/tests/Acceptance/Acceptance.csproj', workspace/'build')
                check = workspace/'evidence'; check.mkdir()
                for file in evidence: (check/file).write_bytes((output/file).read_bytes())
                code = run(name, ['dotnet', str(mutated), 'check', str(check)], allow_failure=True)
                outcome = read(check/'acceptance-result.json')
                detected = (code == 0 and outcome['status'] == 'passed') if old is None else \
                    (code == 1 and outcome['status'] == 'failed' and len(outcome['failures']) > 0 and groups_executed(outcome))
                cases.append({'name':name, 'group':group, 'detected':detected, 'exitCode':code,
                              'assertions':outcome['assertions'], 'failures':outcome['failures'][:20], 'failureCount':len(outcome['failures'])})
                if not detected: raise RuntimeError(('Clean copy did not pass: ' if old is None else 'Mutation was not detected: ')+name)
            report['mutations'] = {'status':'passed', 'cases':cases}
            if hashes() != before:
                raise RuntimeError('Consumed source changed during mutation runs.')
        else:
            report['mutations'] = {'status':'not-requested', 'cases':[row[0] for row in MUTATIONS]}
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
