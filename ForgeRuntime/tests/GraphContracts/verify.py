"""Run current-source cross-language contracts in fresh, non-installed artifacts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[3]
parser = argparse.ArgumentParser()
parser.add_argument('--website', type=Path, default=root.parent / 'Infini-GTFO-Model-Site')
parser.add_argument('--integration', action='store_true')
args = parser.parse_args()
site = args.website.resolve()
if args.integration and not os.environ.get('GTFO_BEPINEX_PATH'):
    parser.error('--integration requires the existing local GTFO_BEPINEX_PATH compile references')
out = Path(tempfile.mkdtemp(prefix='forge-runtime-r4a-verify-'))
artifacts = out / 'artifacts'
results = []

def run(name, command):
    log = out / (name + '.log')
    with log.open('w', encoding='utf-8') as stream:
        process = subprocess.run(list(map(str, command)), cwd=root, stdout=stream, stderr=subprocess.STDOUT)
    results.append({'name': name, 'command': list(map(str, command)), 'exitCode': process.returncode, 'log': str(log)})
    print(f'{name}: exit {process.returncode}', flush=True)
    print('\n'.join(log.read_text(encoding='utf-8-sig', errors='replace').splitlines()[-5:]), flush=True)
    (out / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    return process.returncode

def build(project, name):
    path = root / project
    if run(name + '-build', ['dotnet', 'build', path, '-c', 'Release', '--artifacts-path', artifacts]):
        return None
    if path.suffix == '.sln':
        return True
    assembly = ET.parse(path).getroot().findtext('.//AssemblyName') or path.stem
    return artifacts / 'bin' / path.stem / 'release' / (assembly + '.dll')

def snapshot():
    sources = list((root / 'ForgeRuntime' / 'Framework').glob('*.cs'))
    sources += list((root / 'ForgeRuntime' / 'tests' / 'GraphContracts').glob('*'))
    sources += list((site / 'site' / 'forge').glob('*.ts'))
    sources += [site / 'Tools' / 'register-typescript.ts']
    if args.integration:
        for name in ['ForgeRuntime', 'ForgeDevelopment', 'ForgeEnemy', 'ForgeTrigger', 'ForgeMap', 'ForgeWeapon']:
            for folder, directories, files in os.walk(root / name):
                directories[:] = [d for d in directories if d not in ['bin', 'obj', 'dist', 'artifacts', '__pycache__'] and not d.startswith('.')]
                sources += [Path(folder) / f for f in files if Path(f).suffix in ['.cs', '.csproj']]
        sources.append(root / 'Forge.Architecture.sln')
    return {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources if p.is_file()}

before = snapshot()
(out / 'source-before.json').write_text(json.dumps(before, indent=2), encoding='utf-8')
print('EVIDENCE=' + str(out), flush=True)
vector_path = out / 'vectors.json'
generated = run('typescript', ['node', root / 'ForgeRuntime/tests/GraphContracts/generate.mjs', site, vector_path]) == 0
if generated:
    dll = build('ForgeRuntime/tests/GraphContracts/GraphContracts.csproj', 'graph-contracts')
    if dll:
        run('graph-contracts', ['dotnet', dll, vector_path])
if args.integration:
    host = build('ForgeRuntime/ForgeRuntime.csproj', 'host')
    build('Forge.Architecture.sln', 'architecture-solution')
    fixtures = site / 'Tests/Forge/fixtures/runtime'
    suites = [('Architecture', []), ('Framework', ['--fixtures', fixtures]),
              ('HostIntegration', ['--host', host] if host else []), ('LifecycleWork', ['--fixtures', fixtures]),
              ('EntityObservation', ['--probe-registration']), ('GameBindings', ['--fixtures', fixtures]),
              ('PluginStartup', []), ('HostConfiguration', [])]
    for name, arguments in suites:
        dll = build(f'ForgeRuntime/tests/{name}/{name}.csproj', name)
        if dll:
            run(name, ['dotnet', dll, *arguments])
after = snapshot()
(out / 'source-after.json').write_text(json.dumps(after, indent=2), encoding='utf-8')
drift = sorted(p for p in set(before) | set(after) if before.get(p) != after.get(p))
summary = {'sourceStable': not drift, 'changedSources': drift, 'gameVerified': False,
           'passed': not drift and all(r['exitCode'] == 0 for r in results), 'evidence': str(out)}
(out / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
print(json.dumps(summary), flush=True)
sys.exit(0 if summary['passed'] else 1)
