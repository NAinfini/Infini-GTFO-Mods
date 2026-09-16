"""Read-only website/SDK conformance run; every output stays inside ForgeTrigger/artifacts."""
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

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--site', type=Path, default=ROOT.parents[1] / 'Infini-GTFO-Model-Site')
    parser.add_argument('--out', '--output', dest='output', type=Path,
                        help='Evidence directory; inside ForgeTrigger/artifacts or the system temporary directory.')
    args = parser.parse_args()
    site = args.site.resolve()
    artifacts = (ROOT / 'artifacts').resolve()
    temporary = Path(tempfile.gettempdir()).resolve()
    output = (args.output or artifacts / ('t1-' + datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))).resolve()
    # Not a shared directory: this run owns its evidence directory, so an existing path is a previous run's
    # output (or a tracked file) and is never overwritten.
    if not (output.is_relative_to(artifacts) or output.is_relative_to(temporary)):
        raise ValueError('Output must stay inside ForgeTrigger/artifacts or the system temporary directory.')
    if output.exists():
        raise ValueError('Output must not already exist; every run writes its own directory: ' + str(output))
    output.mkdir(parents=True, exist_ok=False)
    sources = list((ROOT.parent / 'ForgeRuntime/Framework').glob('*.cs'))
    sources += [ROOT / 'ModuleDefinition.cs', ROOT / 'ForgeTrigger.csproj', ROOT / 'tests/Contracts/Program.cs', ROOT / 'tests/Contracts/Contracts.csproj', ROOT / 'tools/t1-contracts.mjs']
    sources += [Path(__file__).resolve(), ROOT.parent / 'ForgeRuntime/Framework/ForgeRuntime.Framework.csproj']
    sources += [ROOT/'tests/Contracts/AuthoringContractTests.cs', ROOT/'tests/Contracts/GraphResolutionChecks.cs', ROOT/'tools/authoring-contract-audit.mjs', ROOT/'tools/graph-metadata-vectors.mjs']
    sources += list((site/'site/forge').rglob('*.ts'))
    sources += list((ROOT / 'tests/fixtures/t1').glob('*.json'))
    sources += [site / 'site/forge' / (name + '.ts') for name in ['contracts','graph','graph-schema','registry','logic-primitives','runtime-contracts','runtime-compiler','runtime-schema','json','target-schema','target-contracts']]
    sources += [site / 'Tools/register-typescript.ts', site / 'catalog/capability-catalog.json', site / 'Tests/Forge/fixtures/runtime/native-manifest.json']
    def hashes() -> dict[str, str]:
        current = set(sources) | set((ROOT.parent / 'ForgeRuntime/Framework').glob('*.cs')) | set((ROOT / 'Pure').rglob('*.cs')) | set((ROOT / 'Targeting').rglob('*.cs'))
        return {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in current}
    before = hashes()
    commands: list[dict] = []
    def run(name: str, argv: list[str]) -> None:
        print('RUN ' + name + ': ' + subprocess.list2cmdline(argv), flush=True)
        log = output / (name + '.log')
        with log.open('w', encoding='utf-8') as stream:
            completed = subprocess.run(argv, cwd=ROOT.parent, stdout=stream, stderr=subprocess.STDOUT, timeout=240, check=False)
        commands.append({'name':name, 'argv':argv, 'exitCode':completed.returncode, 'log':log.name})
        print(log.read_text(encoding='utf-8', errors='replace')[-2000:], flush=True)
        if completed.returncode:
            raise RuntimeError(f'{name} failed with exit {completed.returncode}; full evidence: {log}')
    result: dict = {'status':'failed','gameVerified':False,'commands':commands,'sourcesBefore':before}
    try:
        run('build', ['dotnet','build',str(ROOT / 'tests/Contracts/Contracts.csproj'),'-c','Release','--artifacts-path',str(output / 'build'),'--disable-build-servers'])
        dlls = [p for p in (output / 'build/bin').rglob('ForgeTrigger.ContractTests.dll') if p.with_suffix('.runtimeconfig.json').exists()]
        if len(dlls) != 1:
            raise RuntimeError(f'Expected one runnable contract test DLL, found {dlls}')
        executable = ['dotnet', str(dlls[0])]
        run('export', executable + ['export',str(ROOT),str(output),str(site)])
        run('typescript', ['node',str(ROOT / 'tools/t1-contracts.mjs'),str(ROOT),str(site),str(output)])
        run('csharp', executable + ['check',str(ROOT),str(output),str(site)])
        after = hashes()
        result['sourcesAfter'] = after
        changed = [name for name in before.keys() | after.keys() if before.get(name) != after.get(name)]
        if changed:
            raise RuntimeError('Consumed source changed during validation; rerun on a stable snapshot: ' + ', '.join(changed))
        result['status'] = 'passed'
        result['typescript'] = json.loads((output / 'typescript-result.json').read_text())
        result['csharp'] = json.loads((output / 'csharp-result.json').read_text())
        result['checksStatus'] = 'passed'
        result['authoringCompatibility'] = json.loads((output / 'authoring-compatibility-result.json').read_text(encoding='utf-8'))
        result['runtimeReady'] = False
        result['metadataReady'] = result['authoringCompatibility']['metadataReady']
        result['publicationReady'] = False
        if not result['metadataReady']:
            result['status'] = 'blocked'
            print('CONFORMANCE PASS; runtime authoring compatibility BLOCKED; evidence: ' + str(output), flush=True)
            return 2
        print('PASS; evidence: ' + str(output), flush=True)
        return 0
    except Exception as error:
        result['error'] = str(error)
        print('FAIL: ' + str(error), file=sys.stderr, flush=True)
        return 1
    finally:
        (output / 'summary.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')

if __name__ == '__main__':
    raise SystemExit(main())
