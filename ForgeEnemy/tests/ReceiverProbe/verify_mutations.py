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
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--website', required=True, type=Path,
                        help='the website repository the contract comparison reads its catalog from')
    args = parser.parse_args()
    sdk = args.sdk.resolve(strict=True)
    output = args.output.resolve()
    website = args.website.resolve(strict=True)
    output.mkdir(parents=True, exist_ok=True)
    root = Path(__file__).resolve().parents[3]
    source = (root / 'ForgeEnemy/Native/EnemyModule.cs').read_text(encoding='utf-8-sig')
    damage = (root / 'ForgeEnemy/Native/EnemyModule.Damage.cs').read_text(encoding='utf-8-sig')
    lifecycle = (root / 'ForgeEnemy/Native/EnemyModule.LifecycleFacts.cs').read_text(encoding='utf-8-sig')
    # Entry owns a behaviour watermark, so the module's behaviour partial and the observer it reads are
    # part of the probe too, exactly as the suite's csproj compiles them.
    behaviour = (root / 'ForgeEnemy/Native/EnemyModule.BehaviorFacts.cs').read_text(encoding='utf-8-sig')
    observer = (root / 'ForgeEnemy/Native/Observation/EnemyBehaviorFactsObserver.cs').read_text(encoding='utf-8-sig')
    program = (Path(__file__).parent / 'Program.cs').read_text(encoding='utf-8-sig')
    doubles = (root / 'ForgeRuntime/tests/GameBindings/GameDoubles.cs').read_text(encoding='utf-8-sig')
    shared = [(p.name, p.read_text(encoding='utf-8-sig')) for p in sorted((root / 'ForgeEnemy/tests/Shared').glob('*.cs'))]
    mutations = [
        ('spawn-generation', 'existing.Reference.WorldEpoch == _kernel.WorldEpoch',
         'existing.Reference.WorldEpoch != _kernel.WorldEpoch', 'E2-001.duplicate-spawn'),
        ('observation-replay', 'if (!ReferenceEquals(_owner, owner) || _consumed) return false;',
         'if (!ReferenceEquals(_owner, owner)) return false;', 'E3-003.duplicate-damage-observation'),
        ('despawn-loses-life', 'var entry = Resolve(observation.Target);',
         'var entry = _entities.TryGetValue(ushort.Parse(observation.Target.Id.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture), out var current) ? current : null;',
         'E2-002.captured-life-late-despawn'),
        # The three commit-boundary mutants demote an attempted native write to a known "rejected" row, which the
        # heal aggregation reports as commitState=none: exactly the false claim each named case must catch.
        ('changed-receiver-none',
         '{ rows.Add(Row("unknown", "receiver-changed-during-commit", before)); commitFailed = true; }',
         '{ rows.Add(Row("rejected", "receiver-changed-during-commit", before)); rejected++; continue; }',
         'E3-001.receiver-changed-commit'),
        ('readback-none',
         '{ rows.Add(Row("unknown", "unexpected-health-readback", before)); commitFailed = true; }',
         '{ rows.Add(Row("rejected", "unexpected-health-readback", before)); rejected++; continue; }',
         'E3-002.invalid-readback-commit'),
        ('exception-none',
         'catch (Exception) { rows.Add(Row("unknown", "native-commit-exception", before)); commitFailed = true; committing = false; }',
         'catch (Exception) { rows.Add(Row("rejected", "native-commit-exception", before)); rejected++; continue; }',
         'commit.throw-after-write'),
        # A late callback re-targeted to whatever life currently holds the GlobalID (the old-life guard removed).
        ('late-damage-retargeted', 'var entry = Resolve(before.Target);',
         'var entry = damage.Owner != null && _entities.TryGetValue(damage.Owner.GlobalID, out var current) ? current : null; '
         'if (entry != null) before = new DamageObservation(this, entry.Reference, before.HealthBefore, before.DamagePointer);',
         'identity.late-damage-after-respawn'),
        # health_changed from the native damage window: the observation gate, the sign and the post-call value.
        ('health-gate-lost', ' || _kernel.HasSubscribers(HealthChangedBinding)', '', 'health.damage-window-change'),
        ('health-delta-unsigned', 'delta = -actualDamage', 'delta = actualDamage', 'health.damage-window-change'),
        ('health-value-before', 'value = healthAfter,', 'value = (double)before.HealthBefore,', 'health.damage-window-change'),
        ('health-rise-inferred', 'if (actualDamage == 0) return;',
         'if (actualDamage == 0 && healthAfter <= before.HealthBefore) return;', 'health.damage-window-rise-not-inferred'),
        # The `enemy-type` mount: a reference spelling the id loosely, a type read that outlives the life it was
        # read for, and a kind registered even though the session has no way to read an enemy's block.
        ('enemy-type-loose-reference',
         '=> uint.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out id)\n'
         '            && reference == id.ToString(CultureInfo.InvariantCulture);',
         '=> uint.TryParse(reference, out id);', 'mount.enemy-type-leading-zero'),
        ('enemy-type-stale-life',
         '        // The block getter is native: a callback may retire the life or replace the instance while it is read.\n'
         '        return Resolve(subject) == entry;',
         '        return true;', 'mount.enemy-type-life-retired-while-reading'),
        ('enemy-type-always-registered',
         '            AttachmentMatchers = enemyType == null ? null : new Dictionary<string, AttachmentMatcherRegistration>\n'
         '                { [EnemyTypeAttachment] = AttachmentMatcherRegistration.BySubject(MatchesEnemyType) }',
         '            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>\n'
         '                { [EnemyTypeAttachment] = AttachmentMatcherRegistration.BySubject(MatchesEnemyType) }',
         'mount.enemy-type-unregistered-kind'),
        # The damage action: a client that submits, an unreadable effect reported as a commit, and a limb id that
        # is used as the array index instead of being resolved to one.
        ('damage-nonhost-applies',
         '        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");\n'
         '        var policy = context.Parameters.GetProperty("mitigation_policy").GetString();',
         '        if (false) return CommandResult.Rejected("authority-or-phase");\n'
         '        var policy = context.Parameters.GetProperty("mitigation_policy").GetString();',
         'action.not-host-authority'),
        ('damage-unknown-row-as-committed',
         '                    "rejected" => CommitStates.None,\n'
         '                    _ => CommitStates.Unknown',
         '                    "rejected" => CommitStates.None,\n'
         '                    _ => CommitStates.Confirmed', 'action.unseen-commit-is-unknown'),
        ('damage-unseen-as-commit',
         'if (after == before) { rows.Add(Row("unknown", "damage-unseen", before, after)); unknown++; continue; }',
         'if (after == before) { rows.Add(Row("committed", "committed", before, after)); committed++; continue; }',
         'action.unseen-commit-is-unknown'),
        ('damage-limb-id-as-index',
         '            if (!EnemyNativeWrite.TryNameLimb(damage, limb, out int limbIndex))\n'
         '            { rows.Add(Row("rejected", "invalid-limb", before)); rejected++; continue; }',
         '            int limbIndex = limb;',
         'action.limb-id-resolves-to-index'),
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
        for filename, text in [('Probe.csproj', project), ('Program.cs', program), ('EnemyModule.cs', receiver),
                               ('EnemyModule.Damage.cs', damage),
                               ('EnemyModule.LifecycleFacts.cs', lifecycle),
                               ('EnemyModule.BehaviorFacts.cs', behaviour),
                               ('EnemyBehaviorFactsObserver.cs', observer), ('GameDoubles.cs', doubles), *shared]:
            (case / filename).write_text(text, encoding='utf-8')
        report_path = case / 'report.json'
        command = ['dotnet', 'run', '--project', str(case / 'Probe.csproj'), '-c', 'Release', '--',
                   str(report_path), str(website)]
        completed = subprocess.run(command, capture_output=True, text=True, encoding='utf-8',
                                   errors='replace', timeout=180, check=False)
        (case / 'run.log').write_text(completed.stdout + completed.stderr, encoding='utf-8')
        report = json.loads(report_path.read_text(encoding='utf-8')) if report_path.exists() else {}
        failures = [item['Id'] for item in report.get('checks', []) if not item['Passed']]
        if expected is None:
            status = 'pass' if report and completed.returncode == 0 and report.get('failed') == 0 else 'fail'
        else:
            status = 'pass' if report and completed.returncode == 1 and expected in failures else 'fail'
        results.append({'case': name, 'status': status, 'exitCode': completed.returncode,
                        'expectedFailure': expected, 'actualFailures': failures})
        print(f'{status.upper()} {name}: exit={completed.returncode}, failures={failures}', flush=True)
        if expected is None and status != 'pass':
            break  # Never credit mutants when the unchanged production baseline is broken.
    counts = {s: sum(item['status'] == s for item in results) for s in ('pass', 'fail')}
    summary = {'schemaVersion': 3, 'gameExecuted': False,
               'receiverSourceSha256': hashlib.sha256(source.encode()).hexdigest(),
               'sdkSha256': hashlib.sha256(sdk.read_bytes()).hexdigest(),
               'passed': counts['pass'], 'failed': counts['fail'], 'checks': results}
    (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    complete = len(results) == len(cases)
    verdict = 'FAIL' if counts['fail'] or not complete else 'PASS'
    print(f'{verdict} baseline+mutants {counts["pass"]}/{len(cases)}; failed {counts["fail"]}', flush=True)
    return 0 if complete and counts['fail'] == 0 else 1


if __name__ == '__main__':
    raise SystemExit(main())
