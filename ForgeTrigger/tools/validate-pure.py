# Current-source pure computation conformance; no installation or runtime binding.
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

def framework_sources() -> list[Path]:
    # Every framework source the test project compiles, wherever it sits under Framework/; build outputs are not
    # sources and are never copied into a mutation workspace.
    return [p for p in (ROOT.parent / 'ForgeRuntime/Framework').rglob('*.cs') if not {'obj', 'bin'} & set(p.parts)]

def sources(site: Path) -> list[Path]:
    files = [ROOT / 'ModuleDefinition.cs', ROOT / 'ForgeTrigger.csproj', Path(__file__).resolve(), ROOT / 'tools/pure-vectors.mjs']
    files += list((ROOT / 'Pure').glob('*.cs')) + list((ROOT / 'tests/Pure').glob('*.cs'))
    files += list((ROOT / 'Targeting').glob('*.cs'))
    files += [ROOT / 'tools/collection-vectors.mjs', ROOT / 'tools/modifier-vectors.mjs']
    files += [ROOT / 'tests/Pure/Pure.csproj', ROOT / 'tests/fixtures/pure/cases.json',
              ROOT / 'tests/fixtures/pure/modifier-cases.json']
    files += framework_sources()
    files += [ROOT.parent / 'ForgeRuntime/Framework/ForgeRuntime.Framework.csproj']
    files += list((site / 'site/forge').rglob('*.ts'))
    files += [site / 'Tools/register-typescript.ts', site / 'pnpm-lock.yaml', site / 'node_modules/typescript/package.json']
    return sorted(set(files))

def main() -> int:
    parser = argparse.ArgumentParser(description='Check stateless helpers against the current website.')
    parser.add_argument('--site', type=Path, default=ROOT.parents[1] / 'Infini-GTFO-Model-Site')
    parser.add_argument('--out', '--output', dest='output', type=Path,
                        help='Evidence directory; inside ForgeTrigger/artifacts or the system temporary directory.')
    parser.add_argument('--mutations', action='store_true', help='Build isolated clean/mutated copies; never patch production.')
    args = parser.parse_args()
    site = args.site.resolve()
    artifacts = (ROOT / 'artifacts').resolve()
    temporary = Path(tempfile.gettempdir()).resolve()
    output = (args.output or artifacts / ('pure-' + datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))).resolve()
    # Evidence may live in the repository's own artifacts directory or outside the repository in the system
    # temporary directory. Any other destination could land on a tracked file. The leaf must not exist: a new
    # run never overwrites an earlier one.
    if not (output.is_relative_to(artifacts) or output.is_relative_to(temporary)):
        raise ValueError('Output must stay inside ForgeTrigger/artifacts or the system temporary directory.')
    if output.exists():
        raise ValueError('Output must not already exist; every run writes its own directory: ' + str(output))
    output.mkdir(parents=True, exist_ok=False)
    def hashes() -> dict[str, str]:
        return {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources(site)}
    before: dict[str, str] = {}
    commands: list[dict] = []
    result: dict = {'status':'failed', 'evidenceLevel':'pure-computation-not-runtime-binding', 'gameVerified':False,
                    'sourcesBefore':before, 'commands':commands}
    def run(name: str, argv: list[str], allow_failure: bool = False) -> int:
        log = output / (name + '.log')
        print('RUN ' + name + ': ' + subprocess.list2cmdline(argv), flush=True)
        with log.open('w', encoding='utf-8') as stream:
            process = subprocess.run(argv, cwd=ROOT.parent, stdout=stream, stderr=subprocess.STDOUT, timeout=240, check=False)
        commands.append({'name':name, 'argv':argv, 'exitCode':process.returncode, 'log':log.name})
        print(log.read_text(encoding='utf-8', errors='replace')[-1800:], flush=True)
        if process.returncode and not allow_failure:
            raise RuntimeError(f'{name} exited {process.returncode}; see {log}')
        return process.returncode
    def build(name: str, project: Path, build_output: Path) -> Path:
        run(name, ['dotnet','build',str(project),'-c','Release','--artifacts-path',str(build_output),'--disable-build-servers'])
        matches = [p for p in (build_output / 'bin').rglob('ForgeTrigger.PureTests.dll') if p.with_suffix('.runtimeconfig.json').exists()]
        if len(matches) != 1:
            raise RuntimeError('Expected exactly one runnable PureTests assembly.')
        return matches[0]
    def stable() -> None:
        after = hashes(); result['sourcesAfter'] = after
        changed = [name for name in before.keys() | after.keys() if before.get(name) != after.get(name)]
        if changed:
            raise RuntimeError('Consumed source changed; this run is not a stable integration snapshot: ' + ', '.join(changed))
    def mutation_record(name: str, mutated: bool, code: int, report_path: Path, log: Path) -> dict:
        # A mutation must end in a readable report. The test harness writes one even when a mutant turns a checked
        # refusal into an exception no call site expects; a process that died without one anyway (an uncatchable
        # termination) is recorded from its exit code and log, so the run stays auditable instead of crashing here.
        record = {'name':name, 'mutated':mutated, 'exitCode':code, 'detected':False, 'failures':[]}
        try:
            report = json.loads(report_path.read_text(encoding='utf-8'))
        except (OSError, ValueError) as error:
            record['reportMissing'] = str(error)
            record['logTail'] = log.read_text(encoding='utf-8', errors='replace')[-400:]
            record['detected'] = mutated and code != 0
            return record
        record['failures'] = report['failures']
        record['abnormalExit'] = report.get('abnormalExit', False)
        record['detected'] = (code == 0 and report['status'] == 'passed') if not mutated else (
            code != 0 and report['status'] == 'failed' and len(report['failures']) > 0)
        return record
    try:
        before = hashes(); result['sourcesBefore'] = before
        run('typescript', ['node',str(ROOT / 'tools/pure-vectors.mjs'),str(ROOT),str(site),str(output)])
        run('collection-authoring-fixtures', ['node',str(ROOT / 'tools/collection-vectors.mjs'),str(site),str(output)])
        run('modifier-vectors', ['node',str(ROOT / 'tools/modifier-vectors.mjs'),str(ROOT),str(output)])
        reference = output / 'pure-reference.json'
        assembly = build('build', ROOT / 'tests/Pure/Pure.csproj', output / 'build')
        run('csharp', ['dotnet',str(assembly),str(reference),str(output / 'csharp-result.json')])
        result['typescript'] = {k:v for k,v in json.loads(reference.read_text(encoding='utf-8')).items() if k != 'cases'}
        result['csharp'] = json.loads((output / 'csharp-result.json').read_text(encoding='utf-8'))
        collection_result = json.loads((output / 'collections-reference.json').read_text(encoding='utf-8'))
        result['collections'] = {key:value for key,value in collection_result.items() if key != 'cases'}
        result['collections']['caseCount'] = len(collection_result['cases'])

        stable()
        if args.mutations:
            # Exact source copies are test-only. Never edit a shared SDK or user's working files.
            copy_files = [ROOT / 'ForgeTrigger.csproj', ROOT / 'ModuleDefinition.cs', ROOT / 'tests/Pure/Pure.csproj']
            copy_files += list((ROOT / 'Pure').glob('*.cs')) + list((ROOT / 'tests/Pure').glob('*.cs'))
            copy_files += list((ROOT / 'Targeting').glob('*.cs'))
            copy_files += framework_sources()
            copy_files += [ROOT.parent / 'ForgeRuntime/Framework/ForgeRuntime.Framework.csproj']
            mutations = [
                ('clean-copy', None),
                ('collection-unsorted', ('Pure/ReferenceCollections.cs', 'Array.Sort(keys, result, StringComparer.Ordinal);', '/* Mutant: required sort removed. */')),
                ('limit-sorted', ('Pure/ReferenceCollections.cs', 'Slice(Unique(snapshot).ToArray(), snapshot.Length, count)', 'Slice(Sorted(Unique(snapshot)), snapshot.Length, count)')),
                ('collection-repeats', ('Pure/ReferenceCollections.cs', 'foreach (var reference in candidates) if (seen.Add(reference)) result.Add(reference);', 'foreach (var reference in candidates) result.Add(reference);')),
                ('collection-truncates', ('Pure/ReferenceCollections.cs', 'if (candidates.Count < 0 || candidates.Count > MaximumCandidates)', 'if (false)')),





                ('equal-lerp-regression', ('Pure/ScalarNodes.cs', 'if (a == b) return PureNumbers.Result(a);', '// Mutant: endpoint identity lost.')),
                ('equal-random-regression', ('Pure/SeededNodes.cs', 'if (minimum == maximum) return PureNumbers.Result(minimum);', '// Mutant: endpoint identity lost.')),
                ('midpoint-ties-to-even', ('Pure/ScalarNodes.cs', 'value - lower < 0.5d ? lower : lower + 1d', 'Math.Round(value)')),
                ('wrong-subtraction', ('Pure/ScalarNodes.cs', 'ScalarOperation.Subtract => a - b,', 'ScalarOperation.Subtract => a + b,')),
                ('fixed-seed', ('Pure/SeededStream.cs', 'state = (uint)seed;', 'state = 0u;')),
                ('silent-zero-division', ('Pure/ScalarNodes.cs', 'DivisionZeroPolicy.Reject => throw new RuntimeContractException("pure-division-by-zero", "The divisor must not be zero."),', 'DivisionZeroPolicy.Reject => 0d,')),
                ('tolerance-ignored', ('Pure/ScalarNodes.cs', 'ScalarComparison.Equal => Math.Abs(left - right) <= tolerance,', 'ScalarComparison.Equal => left == right,')),
                ('remap-weight-inverted', ('Pure/MappingNodes.cs', 'var weight = (value - fromMinimum) / (fromMaximum - fromMinimum);', 'var weight = (value - fromMaximum) / (fromMaximum - fromMinimum);')),
                ('curve-domain-clamped', ('Pure/MappingNodes.cs', 'if (value < 0d || value > 1d)', 'if (false)')),
                ('falloff-past-radius', ('Pure/MappingNodes.cs', 'if (distance > radius)', 'if (false)')),
                ('falloff-negative-distance', ('Pure/MappingNodes.cs', 'if (distance < 0d)', 'if (false)')),
                ('normalize-zero-vector-divided', ('Pure/VectorNodes.cs', 'if (length == 0d) return policy switch', 'if (false) return policy switch')),
                ('angle-zero-vector-accepted', ('Pure/VectorNodes.cs', 'if (IsZero(a) || IsZero(b))', 'if (false)')),
            ]
            mutation_results = []
            result['mutations'] = mutation_results
            for name, change in mutations:
                workspace = output / 'mutations' / name
                workspace.mkdir(parents=True, exist_ok=False)
                for source in copy_files:
                    target = workspace / source.relative_to(ROOT.parent)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    data = source.read_bytes()
                    if hashlib.sha256(data).hexdigest() != before[str(source)]:
                        raise RuntimeError('Source changed before mutation snapshot: ' + str(source))
                    target.write_bytes(data)
                if change:
                    relative, old, new = change
                    target = workspace / 'ForgeTrigger' / relative
                    content = target.read_text(encoding='utf-8')
                    if content.count(old) != 1:
                        raise RuntimeError('Mutation anchor is not unique: ' + name)
                    target.write_text(content.replace(old, new), encoding='utf-8')
                try:
                    dll = build('mutation-' + name + '-build', workspace / 'ForgeTrigger/tests/Pure/Pure.csproj', workspace / 'artifacts')
                except RuntimeError as error:
                    # A mutant that does not compile proves nothing about the assertions: it is never counted a kill.
                    mutation_results.append({'name':name, 'mutated':change is not None, 'buildPassed':False,
                                             'detected':False, 'failures':[], 'buildError':str(error)})
                    raise RuntimeError('Mutation does not compile: ' + name) from error
                report_path = workspace / 'result.json'
                code = run('mutation-' + name + '-test', ['dotnet',str(dll),str(reference),str(report_path)], allow_failure=True)
                record = mutation_record(name, change is not None, code, report_path, output / ('mutation-' + name + '-test.log'))
                record['buildPassed'] = True
                mutation_results.append(record)
                if not record['detected']:
                    raise RuntimeError('Mutation validation failed: ' + name + ' (exit ' + str(code) + ')')
        stable()
        result['status'] = 'passed'
        print('PASS; evidence: ' + str(output), flush=True)
        return 0
    except Exception as error:
        result['error'] = str(error)
        print('FAIL: ' + str(error), file=sys.stderr, flush=True)
        return 1
    finally:
        try:
            result['sourcesAfter'] = hashes()
        except Exception as error:
            result['sourceReadError'] = str(error)
        (output / 'summary.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')

if __name__ == '__main__':
    raise SystemExit(main())
