"""Stage E1 in a new ignored directory. Never edit or delete the source worktree."""
from pathlib import Path
import argparse
import difflib
import hashlib
import json
import os

ROOT = Path(__file__).resolve().parents[3]

def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if ROOT / 'ForgeEnemy' / 'obj' not in output.parents:
        raise ValueError('Output must be a new directory below ForgeEnemy/obj.')
    output.mkdir(parents=True, exist_ok=False)
    staged = output / 'workspace'
    originals: dict[str, bytes] = {}
    skipped = {'bin', 'obj', 'dist', 'evidence', 'artifacts', '__pycache__', '.git'}
    for module in ['ForgeRuntime', 'ForgeEnemy', 'ForgeDevelopment', 'ForgeTrigger', 'ForgeMap', 'ForgeWeapon']:
        for parent, directories, files in os.walk(ROOT / module):
            directories[:] = [d for d in directories if d not in skipped]
            for filename in files:
                source = Path(parent) / filename
                if source.suffix not in {'.cs', '.csproj', '.py', '.json', '.sln'}:
                    continue
                relative = source.relative_to(ROOT).as_posix()
                data = source.read_bytes(); originals[relative] = data
                target = staged / relative; target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)
    for source in [ROOT / 'Forge.Architecture.sln']:
        originals[source.name] = source.read_bytes(); (staged / source.name).write_bytes(originals[source.name])
    def replace(relative: str, old: str, new: str, count: int = 1) -> None:
        target = staged / relative; text = target.read_text(encoding='utf-8-sig')
        if text.count(old) != count:
            raise ValueError(f'Cutover precondition changed: {relative}: {old[:70]}')
        target.write_text(text.replace(old, new), encoding='utf-8', newline='')
    bridge = 'ForgeRuntime/GameBindings/GameRuntimeBridge.cs'
    replace(bridge, '    internal static EnemyModule? Enemies { get; private set; }\n', '')
    replace(bridge, '        Enemies = new EnemyModule(Kernel, () => CanExecute, message => Plugin.PluginLog.LogWarning(message));\n', '')
    replace(bridge, '        Enemies?.ClearWorld();\n', '', 2)
    replace(bridge, '        Enemies = null; Kernel = null;', '        Kernel = null;')
    hooks = staged / 'ForgeRuntime/GameBindings/NativeHooks.cs'
    text = hooks.read_text(encoding='utf-8-sig')
    prefix, suffix = text.split('[HarmonyPatch(typeof(EnemySync), nameof(EnemySync.OnSpawn))]', 1)
    if prefix.count('[HarmonyPatch(') != 4 or suffix.count('[HarmonyPatch(') != 2:
        raise ValueError('Hook ownership changed; manual reconciliation required.')
    hooks.write_text(prefix.replace('using Enemies;\n', ''), encoding='utf-8', newline='')
    old_source = 'ForgeRuntime/GameBindings/EnemyModule.cs'
    new_source = 'ForgeEnemy/Native/EnemyModule.cs'
    (staged / old_source).rename(staged / new_source)
    replace(new_source, 'namespace ForgeRuntime.GameBindings;', 'namespace ForgeEnemy.Native;')
    replace('ForgeEnemy/Native/ForgeEnemy.Native.csproj', '../../ForgeRuntime/GameBindings/EnemyModule.cs', 'EnemyModule.cs')
    for relative in ['ForgeEnemy/Native/EnemyPluginSession.cs', 'ForgeEnemy/Native/EnemyNativeHooks.cs']:
        replace(relative, 'using ForgeRuntime.GameBindings;\n', '')
    for target in (staged / 'ForgeEnemy/tests').rglob('*'):
        if target.suffix not in {'.cs', '.csproj', '.py'}:
            continue
        text = target.read_text(encoding='utf-8-sig')
        changed = text.replace(old_source, new_source).replace('using ForgeRuntime.GameBindings;', 'using ForgeEnemy.Native;')
        if changed.count('using ForgeEnemy.Native;\n') > 1:
            first = changed.index('using ForgeEnemy.Native;\n') + len('using ForgeEnemy.Native;\n')
            changed = changed[:first] + changed[first:].replace('using ForgeEnemy.Native;\n', '')
        if changed != text:
            target.write_text(changed, encoding='utf-8', newline='')
    replace('ForgeRuntime/tests/GameBindings/GameBindings.csproj', '../../GameBindings/EnemyModule.cs', '../../../ForgeEnemy/Native/EnemyModule.cs')
    program = 'ForgeRuntime/tests/GameBindings/Program.cs'
    replace(program, 'using ForgeRuntime.GameBindings;', 'using ForgeRuntime.GameBindings;\nusing ForgeEnemy.Native;\nusing Plugin = ForgeRuntime.Plugin;')
    path = staged / program; text = path.read_text(encoding='utf-8')
    if text.count('GameRuntimeBridge.Initialize(') != 3:
        raise ValueError('Bridge setup test count changed.')
    text = text.replace('GameRuntimeBridge.Initialize(', 'InitializeBridge(').replace('GameRuntimeBridge.Enemies', 'bridgeEnemies')
    before = '    void Frame() => GameRuntimeBridge.Guard(GameRuntimeBridge.FixedTick);'
    after = before + '''
    EnemyModule? bridgeEnemies = null;
    void InitializeBridge(string planPath, string permissions)
    {
        bridgeEnemies?.Dispose();
        GameRuntimeBridge.Initialize(planPath, permissions);
        bridgeEnemies = new EnemyModule(GameRuntimeBridge.Kernel!, () => GameRuntimeBridge.CanExecute, message => Plugin.PluginLog.LogWarning(message));
    }'''
    if text.count(before) != 1:
        raise ValueError('Bridge fixture entry changed.')
    text = text.replace(before, after)
    text = text.replace('finally { GameRuntimeBridge.Stop(); SNet.IsMaster = true;',
                        'finally { GameRuntimeBridge.Stop(); bridgeEnemies?.Dispose(); SNet.IsMaster = true;')
    path.write_text(text, encoding='utf-8', newline='')
    replace('ForgeRuntime/tests/GameBindings/NativeEvidence.cs',
        '"FrameworkStateChanged", "FrameworkWorldCleanup", "FrameworkSessionReset", "FrameworkCheckpointRestore", "FrameworkEnemySpawned", "FrameworkEnemyDespawned", "FrameworkEnemyDamage"',
        '"FrameworkStateChanged", "FrameworkWorldCleanup", "FrameworkSessionReset", "FrameworkCheckpointRestore"')
    definition = staged / 'ForgeEnemy/ModuleDefinition.cs'
    definition.write_text('''namespace ForgeEnemy;

// Package identity only. Native/EnemyModule owns the sole executable provider.
public static class ModuleDefinition
{
    public const string ProviderId = "forge.module.gtfo.enemy";
    public const string Version = "1.0.0";
}
''', encoding='utf-8', newline='')
    architecture = 'ForgeRuntime/tests/Architecture/Program.cs'
    replace(architecture, '    (typeof(ForgeEnemy.ModuleDefinition), ForgeEnemy.ModuleDefinition.Create)\n', '')
    replace(architecture, 'assemblies.Distinct().Count() == 6, "five module assemblies and one SDK"',
        'assemblies.Distinct().Count() == 5, "four managed scaffolds and one SDK; native Enemy is checked by NativeLayout"')
    changes = []; patch_parts = []
    paths = set(originals) | {new_source}
    for relative in sorted(paths):
        before_bytes = originals.get(relative)
        target = staged / relative
        after_bytes = target.read_bytes() if target.exists() else None
        if before_bytes == after_bytes:
            continue
        changes.append({'path': relative, 'beforeSha256': sha(before_bytes) if before_bytes is not None else None,
                        'afterSha256': sha(after_bytes) if after_bytes is not None else None})
        before_text = (before_bytes or b'').decode('utf-8-sig').replace('\r\n', '\n')
        after_text = (after_bytes or b'').decode('utf-8-sig').replace('\r\n', '\n')
        patch_parts.extend(difflib.unified_diff(before_text.splitlines(True), after_text.splitlines(True),
            fromfile='a/' + relative if before_bytes is not None else '/dev/null',
            tofile='b/' + relative if after_bytes is not None else '/dev/null'))
    changed_inputs = [p for p, data in originals.items() if not (ROOT / p).exists() or (ROOT / p).read_bytes() != data]
    if changed_inputs:
        raise RuntimeError('Source changed during staging: ' + ', '.join(changed_inputs))
    (output / 'cutover.patch').write_text(''.join(patch_parts), encoding='utf-8', newline='')
    (output / 'cutover-inputs.json').write_text(json.dumps({'schemaVersion': 1,
        'sourceWorktreeUnchanged': True, 'stagedOnly': True, 'gameExecuted': False,
        'changes': changes, 'sourceHashes': {p: sha(data) for p, data in originals.items()}}, indent=2), encoding='utf-8')
    print(f'STAGED {len(changes)} changed paths at {staged}; original worktree unchanged.')
    return 0

if __name__ == '__main__':
    raise SystemExit(main())
