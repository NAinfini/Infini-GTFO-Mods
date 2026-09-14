"""Extract hash-pinned enemy spawn-space facts from an installed GTFO build.

Only Unity serialized files are read (through a caller-supplied UnityPy checkout); the game
is never loaded or executed. The output keeps raw DataBlock values, base-prefab components
and the project NavMesh tables. Interpreting them as a spawn requirement is done once, in
ForgeEnemy/Spawn, and every native semantic that data cannot prove stays unverified there.
Model meshes, bounds and bone previews are deliberately not read: they are not collision or
navigation evidence.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
import struct
import sys
from pathlib import Path

PINS = {
    'appId': '493520',
    'buildId': '20403457',
    'unityPyVersion': '1.25.3',
    'files': {
        'resources.assets': '5833891d4f9d04c8ddb103e2f7feca5c67ed7ec70a7618374fa3cc51640aae5f',
        'sharedassets43.assets': 'd2b5db1128577bdd48f68c61002106fdc60d100ad8b5e542f7748cd7d5db866e',
        'globalgamemanagers': '7825069c90c34fbb49e9e742197d4a6ef2abee09cd60b8193e2a3304c1ff6337',
        'globalgamemanagers.assets': '3bbbdc31f2d9c0a30bd2098a6710606d72708f6f73986d4d10f5a21ee7490ce6',
    },
    # SHA-256 of the encoded TextAsset payload inside resources.assets.
    'textAssets': {
        'enemies': ['GameData.EnemyDataBlock_bin', 2378,
                    '523b99e5895da5e524e981fbce66fb463b8cbdcdc44ffc0de4291dd6c89379b9'],
        'movement': ['GameData.EnemyMovementDataBlock_bin', 2363,
                     'f09ff30301c6dfbc68089c632d2dbd05dc11a51c798b8a60c6284431afe7fdc9'],
        'balancing': ['GameData.EnemyBalancingDataBlock_bin', 2336,
                      'e820f7244d2d2978d922bd7fa4d471bd711055ee1cbc61e80be8f9bb5ea0b0f6'],
    },
    # BuildSettings scene index N owns sharedassetsN.assets; base prefabs live in the enemy shard.
    'enemyShardScene': [43, 'Assets/AssetShards/Scenes/Enemies_S1.unity', 'sharedassets43.assets'],
    'basePrefabDirectory': 'Assets/AssetPrefabs/Characters/Enemies/Bases/',
}
COLLIDERS = ('SphereCollider', 'CapsuleCollider', 'BoxCollider', 'MeshCollider', 'CharacterController')
AIR_GRAPH_SCRIPT = 'AirNavigation.FlyingAirGraphAgent'


class EvidenceError(Exception):
    def __init__(self, code: str, detail: str):
        super().__init__(f'{code}: {detail}')
        self.code = code


def require(condition: bool, code: str, detail: str) -> None:
    if not condition:
        raise EvidenceError(code, detail)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b''):
            digest.update(chunk)
    return digest.hexdigest()


def decode_datablock(raw: bytes):
    """Reverse GTFO's BinaryEncoder byte permutation (BP_Coder seeded with 0x0F921568).

    The seed expands through Unity's xorshift128 integer ranges; decoding replays the swaps
    backwards. A changed format fails JSON parsing instead of producing guessed values.
    """
    state = [0x0F921568]
    for _ in range(3):
        state.append((1812433253 * state[-1] + 1) & 0xffffffff)

    def random() -> int:
        t = (state[0] ^ (state[0] << 11)) & 0xffffffff
        state[:] = [state[1], state[2], state[3], (state[3] ^ (state[3] >> 19) ^ t ^ (t >> 8)) & 0xffffffff]
        return state[3]

    size = 50 + random() % 20
    upper = 47 + random() % 3
    codes = [2 + random() % (upper - 2) for _ in range(size)]
    data = bytearray(raw)
    for index in reversed(range(len(data))):
        other = min(index + codes[index % size], len(data) - 1)
        data[index], data[other] = data[other], data[index]
    return json.loads(data)


def finite(value, label: str) -> float:
    require(isinstance(value, (int, float)) and not isinstance(value, bool) and value == value
            and value not in (float('inf'), float('-inf')), 'value.not-finite', label)
    return value


def extract(game: Path, unitypy, pins: dict) -> dict:
    data = game / 'GTFO_Data'
    manifest = (game.parent.parent / f"appmanifest_{pins['appId']}.acf").read_text(encoding='utf-8')
    field = lambda key: (re.search(r'"' + key + r'"\s+"([^"]+)"', manifest) or [None, None])[1]
    require(field('appid') == pins['appId'], 'steam.app', str(field('appid')))
    require(field('buildid') == pins['buildId'], 'steam.build', str(field('buildid')))
    require(getattr(unitypy, '__version__', None) == pins['unityPyVersion'], 'tool.unitypy', str(getattr(unitypy, '__version__', None)))

    files = []
    for name, expected in sorted(pins['files'].items()):
        path = data / name
        actual = sha256_file(path)
        require(actual == expected, 'hash.' + name, actual)
        files.append({'file': name, 'sha256': actual, 'bytes': path.stat().st_size})

    resources = unitypy.load(str(data / 'resources.assets'))
    by_path = {obj.path_id: obj for obj in resources.objects}
    blocks, text_assets = {}, []
    for key, (name, path_id, expected) in sorted(pins['textAssets'].items()):
        obj = by_path.get(path_id)
        require(obj is not None and obj.type.name == 'TextAsset', 'textasset.missing.' + key, str(path_id))
        asset = obj.read()
        require(asset.m_Name == name, 'textasset.name.' + key, asset.m_Name)
        raw = asset.m_Script.encode('utf-8', errors='surrogateescape')
        actual = hashlib.sha256(raw).hexdigest()
        require(actual == expected, 'textasset.hash.' + key, actual)
        blocks[key] = decode_datablock(raw)['Blocks']
        text_assets.append({'name': name, 'file': 'resources.assets', 'pathId': path_id, 'sha256': actual})

    scene_index, scene_path, shared_name = pins['enemyShardScene']
    env = unitypy.load(str(data / 'globalgamemanagers'), str(data / shared_name), str(data / 'globalgamemanagers.assets'))
    objects: dict[str, dict] = {}
    for obj in env.objects:
        objects.setdefault(Path(obj.assets_file.name).name, {})[obj.path_id] = obj
    managers = objects['globalgamemanagers'].values()

    def single(type_name: str):
        found = [obj for obj in managers if obj.type.name == type_name]
        require(len(found) == 1, 'manager.' + type_name, str(len(found)))
        return found[0].read_typetree()

    scenes = single('BuildSettings')['scenes']
    require(len(scenes) > scene_index and scenes[scene_index] == scene_path, 'shard.scene', str(scenes[scene_index:scene_index + 1]))
    navigation = single('NavMeshProjectSettings')
    agent_types = [{'agentTypeId': s['agentTypeID'], 'radius': finite(s['agentRadius'], 'agentRadius'),
                    'height': finite(s['agentHeight'], 'agentHeight'), 'maxSlope': finite(s['agentSlope'], 'agentSlope'),
                    'stepHeight': finite(s['agentClimb'], 'agentClimb')} for s in navigation['m_Settings']]
    require(len({a['agentTypeId'] for a in agent_types}) == len(agent_types), 'navmesh.agent-type-duplicate', 'agentTypeID')
    areas = [{'index': i, 'name': a['name']} for i, a in enumerate(navigation['areas']) if a['name']]

    shared = objects[shared_name]

    def resolve(owner, pointer):
        name = Path(owner.assets_file.name).name
        if pointer.file_id:
            name = Path(owner.assets_file.externals[pointer.file_id - 1].path).name
        return objects.get(name, {}).get(pointer.path_id)

    def script_name(behaviour) -> str:
        raw = behaviour.get_raw_data()  # MonoBehaviour typetrees are stripped; the header layout is fixed.
        file_id, path_id = struct.unpack_from('<iq', raw, 16)
        owner = Path(behaviour.assets_file.name).name
        script = objects.get(owner if file_id == 0 else Path(behaviour.assets_file.externals[file_id - 1].path).name, {}).get(path_id)
        require(script is not None, 'prefab.script-unresolved', f'{behaviour.path_id}')
        value = script.read()
        return f'{value.m_Namespace}.{value.m_ClassName}' if value.m_Namespace else value.m_ClassName

    def transform_of(game_object):
        found = [resolve(game_object, c.component) for c in game_object.read().m_Component]
        found = [t for t in found if t is not None and t.type.name == 'Transform']
        require(len(found) == 1, 'prefab.transform', str(game_object.path_id))
        return found[0]

    def base_prefab(path: str) -> dict:
        directory = pins['basePrefabDirectory']
        require(path.startswith(directory) and path.endswith('.prefab') and '/' not in path[len(directory):],
                'prefab.path', path)
        name = path[len(directory):-len('.prefab')]
        roots = [obj for obj in shared.values() if obj.type.name == 'GameObject' and obj.peek_name() == name]
        require(len(roots) == 1, 'prefab.name-not-unique', f'{name}: {len(roots)}')
        root = roots[0]
        require(transform_of(root).read().m_Father.path_id == 0, 'prefab.not-root', name)
        agents, scripts, colliders = [], set(), []

        def visit(game_object, hierarchy: str, active: bool) -> None:
            value = game_object.read()
            active = active and bool(value.m_IsActive)
            for reference in value.m_Component:
                component = resolve(game_object, reference.component)
                require(component is not None, 'prefab.component-unresolved', hierarchy)
                kind = component.type.name
                if kind == 'NavMeshAgent':
                    tree = component.read_typetree()
                    agents.append({'hierarchy': hierarchy, 'enabled': bool(tree['m_Enabled']), 'agentTypeId': tree['m_AgentTypeID'],
                                   'radius': finite(tree['m_Radius'], 'm_Radius'), 'height': finite(tree['m_Height'], 'm_Height'),
                                   'baseOffset': finite(tree['m_BaseOffset'], 'm_BaseOffset'), 'walkableMask': tree['m_WalkableMask'],
                                   'autoTraverseOffMeshLink': bool(tree['m_AutoTraverseOffMeshLink']),
                                   'obstacleAvoidanceType': tree['m_ObstacleAvoidanceType'], 'avoidancePriority': tree['avoidancePriority']})
                elif kind == 'MonoBehaviour':
                    scripts.add(script_name(component))
                elif kind in COLLIDERS:
                    tree = component.read_typetree()
                    for ignored in ('m_GameObject', 'm_Material', 'm_Mesh'):
                        tree.pop(ignored, None)
                    colliders.append({'hierarchy': hierarchy, 'type': kind, 'activeInHierarchy': active, 'fields': tree})
            for child in transform_of(game_object).read().m_Children:
                child_transform = resolve(game_object, child).read()
                child_object = resolve(game_object, child_transform.m_GameObject)
                visit(child_object, hierarchy + '/' + child_object.peek_name(), active)

        visit(root, name, True)
        require(len(agents) <= 1, 'prefab.multiple-navmesh-agents', name)
        return {'path': path, 'object': {'file': shared_name, 'pathId': str(root.path_id)},
                'navMeshAgent': agents[0] if agents else None, 'airGraphAgent': AIR_GRAPH_SCRIPT in scripts,
                'scripts': sorted(scripts), 'colliders': colliders}

    def unique(rows: list, label: str) -> list:
        require(len({row['id'] for row in rows}) == len(rows), 'datablock.duplicate-id.' + label, label)
        return sorted(rows, key=lambda row: row['id'])

    movement = unique([{'id': b['persistentID'], 'name': b['name'], 'internalEnabled': b['internalEnabled'],
                        'locomotionPathMove': b['LocomotionPathMove'], 'allowClimbDownLadders': b['AllowClimbDownLadders']}
                       for b in blocks['movement']], 'movement')
    balancing = unique([{'id': b['persistentID'], 'name': b['name'], 'internalEnabled': b['internalEnabled'],
                         'enemyCollisionRadius': finite(b['EnemyCollisionRadius'], 'EnemyCollisionRadius'),
                         'canBePushed': b['CanBePushed']} for b in blocks['balancing']], 'balancing')
    enemies = unique([{'id': b['persistentID'], 'name': b['name'], 'internalEnabled': b['internalEnabled'],
                       'enemyType': b['EnemyType'], 'basePrefabs': list(b['BasePrefabs']),
                       'movementDataId': b['MovementDataId'], 'balancingDataId': b['BalancingDataId'],
                       'sizeRanges': [{'min': finite(m['SizeRange']['x'], 'SizeRange.x'), 'max': finite(m['SizeRange']['y'], 'SizeRange.y')}
                                      for m in b['ModelDatas']],
                       'modelFiles': [m['ModelFile'] for m in b['ModelDatas']],
                       'arenaDimensions': list(b['ArenaDimensions']), 'isCocoon': b['isCoccoon']}
                      for b in blocks['enemies']], 'enemies')
    bases = [base_prefab(path) for path in sorted({p for enemy in enemies for p in enemy['basePrefabs']})]
    return {
        'schemaVersion': 1, 'kind': 'gtfo-enemy-spawn-space-evidence', 'build': pins['buildId'], 'gameExecuted': False,
        'tool': {'unityPy': pins['unityPyVersion']},
        'inputs': {'files': files, 'textAssets': text_assets,
                   'enemyShardScene': {'buildIndex': scene_index, 'path': scene_path, 'sharedAssets': shared_name}},
        'navMeshAgentTypes': agent_types, 'navMeshAreas': areas, 'basePrefabs': bases,
        'movementBlocks': movement, 'balancingBlocks': balancing, 'enemies': enemies,
    }


def load_unitypy(path: Path):
    sys.path.insert(0, str(path.resolve(strict=True)))
    import UnityPy  # noqa: PLC0415 - the checkout is an explicit, pinned argument
    return UnityPy


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, required=True, help='GTFO install root (steamapps/common/GTFO)')
    parser.add_argument('--unitypy', type=Path, required=True, help='directory containing the pinned UnityPy package')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        evidence = extract(args.game.resolve(strict=True), load_unitypy(args.unitypy), copy.deepcopy(PINS))
    except EvidenceError as error:
        print('FAIL ' + str(error), file=sys.stderr)
        return 1
    args.output.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(evidence, indent=2, sort_keys=True, ensure_ascii=False) + '\n'
    args.output.write_text(text, encoding='utf-8', newline='\n')
    print(f"PASS {len(evidence['enemies'])} enemy blocks, {len(evidence['basePrefabs'])} base prefabs; "
          f"sha256={hashlib.sha256(text.encode('utf-8')).hexdigest()}; game NOT executed.")
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
