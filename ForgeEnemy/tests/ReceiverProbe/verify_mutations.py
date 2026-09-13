"""Compile isolated receiver mutants; never mutate production or shared build outputs."""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
import xml.sax.saxutils as xml


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk', required=True, type=Path)
    parser.add_argument('--fixtures', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    sdk = args.sdk.resolve(strict=True)
    fixtures = args.fixtures.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    root = Path(__file__).resolve().parents[3]
    source = (root / 'ForgeEnemy/Native/EnemyModule.cs').read_text(encoding='utf-8-sig')
    lifecycle = (root / 'ForgeEnemy/Native/EnemyModule.LifecycleFacts.cs').read_text(encoding='utf-8-sig')
    program = (Path(__file__).parent / 'Program.cs').read_text(encoding='utf-8-sig')
    doubles = (root / 'ForgeRuntime/tests/GameBindings/GameDoubles.cs').read_text(encoding='utf-8-sig')
    mutations = [
        ('spawn-generation', 'existing.Reference.WorldEpoch == _kernel.WorldEpoch',
         'existing.Reference.WorldEpoch != _kernel.WorldEpoch', 'E2-001.duplicate-spawn'),
        ('observation-replay', 'if (!ReferenceEquals(_owner, owner) || _consumed) return false;',
         'if (!ReferenceEquals(_owner, owner)) return false;', 'E3-003.duplicate-damage-observation'),
        ('despawn-loses-life', 'var entry = Resolve(observation.Target);',
         'var entry = _entities.TryGetValue(ushort.Parse(observation.Target.Id.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture), out var current) ? current : null;',
         'E2-002.captured-life-late-despawn'),
        ('changed-receiver-none', 'CommandResult.FailedUnknown(attempted, "gtfo.enemy.receiver_changed_during_commit")',
         'CommandResult.Failed("gtfo.enemy.receiver_changed_during_commit")', 'E3-001.receiver-changed-commit'),
        ('readback-none', 'CommandResult.FailedUnknown(attempted, "gtfo.enemy.unexpected_health_readback")',
         'CommandResult.Failed("gtfo.enemy.unexpected_health_readback")', 'E3-002.invalid-readback-commit'),
        ('exception-none', 'CommandResult.FailedUnknown(attempted, "gtfo.enemy.native_commit_exception", error.GetType().Name)',
         'CommandResult.Failed("gtfo.enemy.native_commit_exception", error.GetType().Name)', 'commit.throw-after-write'),
    ]
    cases = [('baseline', source, None)]
    for name, old, new, expected in mutations:
        if source.count(old) != 1:
            raise ValueError(f'Mutation no longer matches exactly once: {name}')
        cases.append((name, source.replace(old, new), expected))
    project = '''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net6.0</TargetFramework>
<LangVersion>latest</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
<CheckEolTargetFramework>false</CheckEolTargetFramework></PropertyGroup>
<ItemGroup><Reference Include="ForgeRuntime.Framework"><HintPath>SDK_PATH</HintPath></Reference>
<None Include="EnemyModule.cs" Link="EnemyModule.source.cs" CopyToOutputDirectory="Always" /></ItemGroup>
</Project>'''.replace('SDK_PATH', xml.escape(str(sdk)))
    results = []
    for name, receiver, expected in cases:
        case = output / name
        case.mkdir(exist_ok=False)
        for filename, text in [('Probe.csproj', project), ('Program.cs', program),
                               ('EnemyModule.cs', receiver), ('EnemyModule.LifecycleFacts.cs', lifecycle), ('GameDoubles.cs', doubles)]:
            (case / filename).write_text(text, encoding='utf-8')
        report_path = case / 'report.json'
        command = ['dotnet', 'run', '--project', str(case / 'Probe.csproj'), '-c', 'Release',
                   '--', str(fixtures), str(report_path)]
        completed = subprocess.run(command, capture_output=True, text=True, encoding='utf-8',
                                   errors='replace', timeout=120, check=False)
        (case / 'run.log').write_text(completed.stdout + completed.stderr, encoding='utf-8')
        report = json.loads(report_path.read_text(encoding='utf-8')) if report_path.exists() else {}
        failures = [item['Id'] for item in report.get('checks', []) if not item['Passed']]
        passed = bool(report) and (completed.returncode == 0 and report.get('failed') == 0
                                  if expected is None else completed.returncode == 1 and expected in failures)
        results.append({'case': name, 'passed': passed, 'exitCode': completed.returncode,
                        'expectedFailure': expected, 'actualFailures': failures})
        print(f'{"PASS" if passed else "FAIL"} {name}: exit={completed.returncode}, failures={failures}', flush=True)
        if expected is None and not passed:
            break  # Never credit mutants when the unchanged production baseline is broken.
    summary = {'schemaVersion': 1, 'gameExecuted': False,
               'receiverSourceSha256': hashlib.sha256(source.encode()).hexdigest(),
               'sdkSha256': hashlib.sha256(sdk.read_bytes()).hexdigest(),
               'passed': sum(item['passed'] for item in results),
               'failed': sum(not item['passed'] for item in results), 'checks': results}
    (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    return 0 if len(results) == len(cases) and all(item['passed'] for item in results) else 1


if __name__ == '__main__':
    raise SystemExit(main())
