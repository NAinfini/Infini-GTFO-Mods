"""Verify a proposed SDK repair in a disposable workspace; never modify the shared Runtime."""
from __future__ import annotations
import argparse
import datetime
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--site', type=Path, default=ROOT.parents[1] / 'Infini-GTFO-Model-Site')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--proposal', type=Path, default=ROOT / 'handoff/r3-observer-candidate.json')
    parser.add_argument('--mutations', action='store_true')
    args = parser.parse_args(); site = args.site.resolve()
    output = (args.output or ROOT / 'artifacts' / ('r3-candidate-' + datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))).resolve()
    if not output.is_relative_to((ROOT / 'artifacts').resolve()):
        raise ValueError('Candidate output must be inside ForgeTrigger/artifacts.')
    output.mkdir(parents=True, exist_ok=False)
    spec = importlib.util.spec_from_file_location('trigger_gate_sources', ROOT / 'tools/validate-trigger.py')
    if spec is None or spec.loader is None: raise RuntimeError('Cannot load source inventory.')
    inventory = importlib.util.module_from_spec(spec); spec.loader.exec_module(inventory)
    proposal_file = args.proposal.resolve()
    if not proposal_file.is_relative_to((ROOT / 'handoff').resolve()):
        raise ValueError('Proposal must be an existing Trigger handoff file.')
    proposal = json.loads(proposal_file.read_text(encoding='utf-8'))
    report = {'status': 'failed', 'evidenceLevel': 'isolated-candidate-not-working-tree-integration', 'gameVerified': False}
    def hashes() -> dict[str, str]:
        paths = inventory.sources(site) + [proposal_file]
        return {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
    before = hashes(); report['sourcesBefore'] = before
    workspace = output / 'workspace'
    try:
        if proposal['schemaVersion'] != 1 or proposal['status'] != 'proposed-not-applied':
            raise ValueError('Unexpected candidate proposal format.')
        for relative, expected in proposal['baseSha256'].items():
            source = (ROOT.parent / relative).resolve()
            if not source.is_relative_to((ROOT.parent / 'ForgeRuntime/Framework').resolve()):
                raise ValueError('Proposal must address the public SDK only.')
            if hashlib.sha256(source.read_bytes()).hexdigest() != expected:
                raise RuntimeError('SDK changed since proposal: ' + relative)
        for source in inventory.sources(site):
            if not source.is_relative_to(ROOT.parent) or source.is_relative_to(site): continue
            destination = workspace / source.relative_to(ROOT.parent)
            destination.parent.mkdir(parents=True, exist_ok=True)
            data = source.read_bytes()
            if hashlib.sha256(data).hexdigest() != before[str(source)]: raise RuntimeError('Source drift during copy.')
            destination.write_bytes(data)
        for edit in proposal['edits']:
            destination = (workspace / edit['path']).resolve()
            if not destination.is_relative_to((workspace / 'ForgeRuntime/Framework').resolve()):
                raise ValueError('Candidate edit escaped its copied SDK.')
            text = destination.read_text(encoding='utf-8-sig')
            if text.count(edit['before']) != 1: raise RuntimeError('Ambiguous candidate edit: ' + edit['path'])
            destination.write_text(text.replace(edit['before'], edit['after']), encoding='utf-8')
        report['candidateHashes'] = {str(p.relative_to(workspace)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in (workspace / 'ForgeRuntime/Framework').glob('*') if p.is_file()}
        gate = workspace / 'ForgeTrigger/artifacts/candidate-gate'
        argv = [sys.executable, '-X', 'utf8', '-u', str(workspace / 'ForgeTrigger/tools/validate-trigger.py'),
                '--site', str(site), '--output', str(gate)]
        if args.mutations: argv.append('--mutations')
        print('RUN isolated candidate full gate', flush=True)
        with (output / 'candidate-console.log').open('w', encoding='utf-8') as stream:
            process = subprocess.run(argv, cwd=workspace, stdout=stream, stderr=subprocess.STDOUT, timeout=600)
        report['command'] = argv; report['exitCode'] = process.returncode
        gate_report = json.loads((gate / 'summary.json').read_text(encoding='utf-8'))
        report['gate'] = gate_report; report['sourcesAfter'] = hashes()
        if report['sourcesAfter'] != before: raise RuntimeError('Working source changed during candidate verification.')
        report['sharedRuntimeModified'] = False
        if process.returncode != 0 or gate_report['status'] != 'passed':
            raise RuntimeError('Candidate gate failed; retain its detailed evidence.')
        report['status'] = 'passed'
        print('PASS isolated proposal; shared Runtime is unchanged: ' + str(output), flush=True)
        return 0
    except Exception as error:
        report['error'] = str(error); print('FAIL: ' + str(error), file=sys.stderr, flush=True)
        return 1
    finally:
        report['sourcesAfter'] = hashes()
        (output / 'summary.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')

if __name__ == '__main__':
    raise SystemExit(main())
