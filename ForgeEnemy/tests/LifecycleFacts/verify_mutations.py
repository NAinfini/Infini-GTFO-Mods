"""Compile isolated negative variants; never rewrite production sources."""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
from xml.sax.saxutils import escape


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk', type=Path, required=True)
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    sdk = args.sdk.resolve(strict=True)
    fixtures = args.fixtures.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    root = Path(__file__).resolve().parents[3]
    native = root / 'ForgeEnemy/Native'
    files = list(native.glob('*.cs')) + list((native / 'Observation').glob('*.cs'))
    files += list(Path(__file__).parent.glob('*.cs'))
    files += [root / ('ForgeEnemy/tests/NativePlugin/' + name) for name in ['GameDoubles.cs', 'LoaderDoubles.cs']]
    snapshots = {p.name: p.read_bytes() for p in files}
    if len(snapshots) != len(files):
        raise ValueError('Conflicting source names; reconcile the harness before running.')
    source = snapshots['EnemyModule.LifecycleFacts.cs'].decode('utf-8-sig')
    mutations = [
        ('death-reentry', '|| entry.DeathObservation != null', '', 1, 'death.duplicate-and-nested'),
        ('token-replay', 'if (!ReferenceEquals(_owner, owner) || _consumed) return false;',
         'if (!ReferenceEquals(_owner, owner)) return false;', 1, 'limb.transition-once'),
        ('death-without-readback', '|| enemy.Alive', '', 2, 'death.alive-readback-is-not-death'),
        ('noop-is-break', 'if (!destroyed)', 'if (false)', 1, 'limb.noop-then-genuine-transition'),
        ('owner-id-lost', '|| damage.Owner.GlobalID != entry.Enemy.GlobalID', '', 1,
         'limb.changed-after-capture-owner-id-mismatch'),
        ('indexed-pointer-lost', 'indexed.Pointer == observation.LimbPointer', 'true', 1,
         'limb.changed-after-capture-new-indexed-part'),
    ]
    cases = [('baseline', source, None)]
    for name, old, new, count, expected in mutations:
        if source.count(old) != count:
            raise ValueError('Mutation no longer matches: ' + name)
        cases.append((name, source.replace(old, new), expected))
    project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
    project += '<TargetFramework>net6.0</TargetFramework><LangVersion>latest</LangVersion><Nullable>enable</Nullable>'
    project += '<ImplicitUsings>enable</ImplicitUsings><CheckEolTargetFramework>false</CheckEolTargetFramework></PropertyGroup>'
    project += '<ItemGroup><Reference Include="ForgeRuntime.Framework"><HintPath>' + escape(str(sdk))
    project += '</HintPath></Reference></ItemGroup></Project>'
    results = []
    for name, text, expected in cases:
        case = output / name
        case.mkdir()
        for filename, data in snapshots.items():
            (case / filename).write_bytes(data)
        (case / 'EnemyModule.LifecycleFacts.cs').write_text(text, encoding='utf-8')
        (case / 'Probe.csproj').write_text(project, encoding='utf-8')
        report_path = case / 'result.json'
        command = ['dotnet', 'run', '--project', str(case / 'Probe.csproj'), '-c', 'Release',
                   '--', str(fixtures), str(report_path)]
        result = subprocess.run(command, capture_output=True, text=True, encoding='utf-8',
                                errors='replace', timeout=120, check=False)
        (case / 'run.log').write_text(result.stdout + result.stderr, encoding='utf-8')
        report = json.loads(report_path.read_text(encoding='utf-8')) if report_path.exists() else {}
        failed = [row['Id'] for row in report.get('checks', []) if not row['Passed']]
        valid = bool(report) and (result.returncode == 0 and not failed if expected is None
                                  else result.returncode == 1 and expected in failed)
        results.append({'case': name, 'passed': valid, 'exitCode': result.returncode,
                        'expectedFailure': expected, 'actualFailures': failed, 'command': command})
        print(f'{"PASS" if valid else "FAIL"} {name}: {failed}', flush=True)
        if expected is None and not valid:
            break
    summary = {'schemaVersion': 1, 'gameExecuted': False, 'checks': results,
               'sourceHashes': {n: hashlib.sha256(b).hexdigest() for n, b in snapshots.items()},
               'sdkSha256': hashlib.sha256(sdk.read_bytes()).hexdigest(),
               'passed': sum(r['passed'] for r in results), 'failed': sum(not r['passed'] for r in results)}
    (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    return 0 if len(results) == len(cases) and all(r['passed'] for r in results) else 1


if __name__ == '__main__':
    raise SystemExit(main())
