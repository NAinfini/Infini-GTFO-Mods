"""Run the real spawn-space extractor against deliberately wrong pins.

A baseline must pass first; each mutant must then fail with its exact error code. The
installed game and UnityPy checkout are read-only inputs; nothing is written beside them.
"""
from __future__ import annotations

import argparse
import copy
import importlib.util
import json
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--unitypy', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('extract_spawn_space', Path(__file__).with_name('extract_spawn_space.py'))
    tool = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(tool)
    unitypy = tool.load_unitypy(args.unitypy)
    game = args.game.resolve(strict=True)

    def mutant(change):
        pins = copy.deepcopy(tool.PINS)
        change(pins)
        return pins

    cases = [
        ('baseline', copy.deepcopy(tool.PINS), None),
        ('wrong-build', mutant(lambda p: p.__setitem__('buildId', '0')), 'steam.build'),
        ('wrong-unitypy', mutant(lambda p: p.__setitem__('unityPyVersion', '0.0.0')), 'tool.unitypy'),
        ('wrong-file-hash', mutant(lambda p: p['files'].__setitem__('sharedassets43.assets', '0' * 64)), 'hash.sharedassets43.assets'),
        ('wrong-block-hash', mutant(lambda p: p['textAssets']['movement'].__setitem__(2, '0' * 64)), 'textasset.hash.movement'),
        ('wrong-block-name', mutant(lambda p: p['textAssets']['balancing'].__setitem__(0, 'GameData.EnemySFX_bin')), 'textasset.name.balancing'),
        ('wrong-shard-scene', mutant(lambda p: p['enemyShardScene'].__setitem__(1, 'Assets/AssetShards/Scenes/Enemies_S2.unity')), 'shard.scene'),
        ('wrong-prefab-directory', mutant(lambda p: p.__setitem__('basePrefabDirectory', 'Assets/AssetPrefabs/Characters/Enemies/')), 'prefab.path'),
    ]
    results = []
    for name, pins, expected in cases:
        try:
            evidence = tool.extract(game, unitypy, pins)
            code, detail = None, f"{len(evidence['enemies'])} enemies"
        except tool.EvidenceError as error:
            code, detail = error.code, str(error)
        passed = code is None if expected is None else code == expected
        results.append({'case': name, 'passed': passed, 'expectedFailure': expected, 'actualFailure': code, 'detail': detail})
        print(f"{'PASS' if passed else 'FAIL'} {name}: {code or detail}", flush=True)
        if expected is None and not passed:
            break  # a broken baseline cannot credit any rejection
    summary = {'schemaVersion': 1, 'gameExecuted': False, 'passed': sum(r['passed'] for r in results),
               'failed': sum(not r['passed'] for r in results), 'checks': results}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')
    print(f"{'PASS' if summary['failed'] == 0 and len(results) == len(cases) else 'FAIL'} "
          f"{summary['passed']}/{len(cases)} extractor pin checks; game NOT executed.")
    return 0 if summary['failed'] == 0 and len(results) == len(cases) else 1


if __name__ == '__main__':
    raise SystemExit(main())
