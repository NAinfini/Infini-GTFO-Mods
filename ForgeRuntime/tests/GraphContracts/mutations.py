"""Prove focused tests reject broken copies; never edit production for mutation tests."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[3]
if len(sys.argv) != 2:
    raise SystemExit('Usage: mutations.py <fresh generated vectors.json>')
vectors = Path(sys.argv[1]).resolve()
if not vectors.is_file():
    raise SystemExit('Vector file does not exist')
out = Path(tempfile.mkdtemp(prefix='forge-runtime-r4a-mutations-'))
changes = [
    ('control', None, None, None),
    ('dropped-last-port', 'RuntimeGraphContracts.cs', 'i <= count; i++) ports.Add(', 'i < count; i++) ports.Add('),
    ('sorted-port-order', 'RuntimeGraphContracts.cs', 'ports.Add(Port(spec.GetProperty("port"), i));',
     'ports.Add(Port(spec.GetProperty("port"), i)); ports = ports.OrderBy(p => RuntimeJson.Text(p, "id"), StringComparer.Ordinal).ToList();'),
    ('raised-count-ceiling', 'RuntimeGraphContracts.cs', 'MaximumVariadicPorts = 32;', 'MaximumVariadicPorts = 33;'),
    ('missing-collision-check', 'RuntimeGraphContracts.cs', '!ids.Contains(added)', 'true'),
    ('plan-skips-expansion', 'RuntimePlan.cs', 'var contract = RuntimeGraphContracts.Resolve(graph, parameters);',
     'var contract = graph;')]
results = []
print('EVIDENCE=' + str(out), flush=True)
base = out / 'base'
for directory in ['ForgeRuntime/Framework', 'ForgeRuntime/tests/GraphContracts']:
    target = base / directory
    target.mkdir(parents=True)
    for file in (root / directory).iterdir():
        if file.is_file() and file.suffix in ['.cs', '.csproj']:
            shutil.copy2(file, target / file.name)
for name, filename, old, new in changes:
    case = out / name
    shutil.copytree(base, case / 'source')
    if filename:
        file = case / 'source/ForgeRuntime/Framework' / filename
        text = file.read_text(encoding='utf-8-sig')
        if text.count(old) != 1:
            raise RuntimeError('Mutation anchor changed: ' + name)
        file.write_text(text.replace(old, new), encoding='utf-8')
    project = case / 'source/ForgeRuntime/tests/GraphContracts/GraphContracts.csproj'
    artifacts = case / 'artifacts'
    with (case / 'build.log').open('wb') as log:
        build = subprocess.run(['dotnet', 'build', str(project), '-c', 'Release',
                                '--artifacts-path', str(artifacts)], stdout=log, stderr=subprocess.STDOUT)
    test_exit = None
    if build.returncode == 0:
        dll = artifacts / 'bin/GraphContracts/release/GraphContracts.dll'
        with (case / 'test.log').open('wb') as log:
            test_exit = subprocess.run(['dotnet', str(dll), str(vectors)], stdout=log, stderr=subprocess.STDOUT).returncode
    text = (case / 'test.log').read_text(encoding='utf-8-sig', errors='replace') if test_exit is not None else ''
    reached_assertions = 'Graph contract checks:' in text
    passed = build.returncode == 0 and reached_assertions and (
        test_exit == 0 if name == 'control' else test_exit != 0 and 'FAIL ' in text)
    results.append({'case': name, 'buildExit': build.returncode, 'testExit': test_exit,
                    'reachedAssertions': reached_assertions, 'expectedOutcome': passed, 'directory': str(case)})
    print(json.dumps(results[-1]), flush=True)
    (out / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
passed = all(r['expectedOutcome'] for r in results)
print(json.dumps({'passed': passed, 'mutantsDetected': sum(r['expectedOutcome'] for r in results[1:]),
                  'evidence': str(out), 'gameVerified': False}), flush=True)
sys.exit(0 if passed else 1)
