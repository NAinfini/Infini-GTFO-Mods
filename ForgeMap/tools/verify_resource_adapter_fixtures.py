"""Check resource-side Adapter descriptor fixtures against GENERATION-SPEC.md section 3.

Fixture-schema evidence only: reads JSON, never loads a bundle, game assembly or uploaded DLL.
A valid descriptor is not a generation success; unknown spatial data is reported as a blocker.
"""
from __future__ import annotations
import argparse
import json
import math
import re
import sys
from pathlib import Path
from typing import Any
from urllib.parse import quote

HASH = re.compile(r'[0-9a-f]{64}')
PIN = re.compile(r'([A-Za-z0-9_]+-[A-Za-z0-9_]+)-(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)')
PATH_ID = re.compile(r'0|-?[1-9]\d*')
# Profiles are verified resource protocols, never package names. Candidate until MAP-ADAPTER game evidence.
PROFILES = {'gtfo.complex-resource-geomorph': 1}
# Static descriptors never carry game verification; that belongs to a runtime receipt.
EVIDENCE = {'fixture-synthetic', 'extracted-metadata'}
SPATIAL = ('colliders', 'navigation', 'occlusion')
SPATIAL_RULES = {
    'colliders': ({'source', 'unknown'}, {'node', 'shape', 'evidence'}),
    'navigation': ({'build-time', 'unknown'}, {'node', 'kind'}),
    'occlusion': ({'source', 'unknown'}, {'node', 'kind', 'area'}),
}
MAX_NODES = 50000


class DescriptorError(Exception):
    def __init__(self, code: str, detail: str) -> None:
        super().__init__(code + ': ' + detail)
        self.code = code


def fail(code: str, detail: str) -> None:
    raise DescriptorError('descriptor.' + code, detail)


def need(ok: bool, code: str, detail: str) -> None:
    if not ok:
        fail(code, detail)


def fields(value: Any, allowed: set[str], path: str) -> dict[str, Any]:
    need(isinstance(value, dict), 'schema', path + ' must be an object')
    need(set(value) <= allowed, 'unknown-field', path + ': ' + ', '.join(sorted(set(value) - allowed)))
    need(set(value) == allowed, 'schema', path + ' missing ' + ', '.join(sorted(allowed - set(value))))
    return value


def text(value: Any, limit: int = 2048) -> bool:
    return isinstance(value, str) and 0 < len(value) <= limit and value.strip() == value \
        and not any(ord(c) < 32 for c in value)


def finite_number(value: Any) -> bool:
    # JSON integers are arbitrary precision in Python; an int too large for a float must reject, not raise.
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return False
    try:
        return math.isfinite(value)
    except OverflowError:
        return False


def vector(value: Any, size: int) -> bool:
    return isinstance(value, list) and len(value) == size and all(finite_number(n) for n in value)


def source_object(value: Any, path: str) -> tuple[str, str]:
    fields(value, {'file', 'pathId'}, path)
    need(text(value['file'], 512) and '/' not in value['file'], 'source', path + '.file')
    path_id = value['pathId']
    need(isinstance(path_id, str) and PATH_ID.fullmatch(path_id) is not None and len(path_id) <= 20
         and -(1 << 63) <= int(path_id) < (1 << 63), 'path-id', path + '.pathId must be an int64 decimal string')
    return value['file'], path_id


def imported_id(pin: str, bundle: str, file: str, path_id: str) -> str:
    # Mirrors the website's importedResourceReference (encodeURIComponent per segment).
    name = PIN.fullmatch(pin).group(1)
    return 'forge.imported:' + ':'.join(quote(part, safe="-_.!~*'()") for part in (name, bundle, file, path_id))


def transform(value: Any, path: str) -> None:
    fields(value, {'position', 'rotation', 'scale'}, path)
    need(vector(value['position'], 3) and vector(value['rotation'], 4) and vector(value['scale'], 3)
         and all(n != 0 for n in value['scale'])
         and abs(math.hypot(*value['rotation']) - 1) <= 1e-4, 'transform', path)


def shared_bytes(rows: Any, path: str) -> set[str]:
    need(isinstance(rows, list), 'schema', path + ' must be a list')
    keys: set[str] = set()
    for index, row in enumerate(rows):
        row_path = f'{path}[{index}]'
        need(isinstance(row, dict) and row.get('kind') in ('mesh', 'texture', 'material'), 'shared-bytes', row_path + '.kind')
        fields(row, {'key', 'kind', 'sha256', 'attributes'} if row['kind'] == 'mesh' else {'key', 'kind', 'sha256'}, row_path)
        need(isinstance(row['sha256'], str) and HASH.fullmatch(row['sha256']) is not None
             and row['key'] == row['kind'] + ':' + row['sha256'] and row['key'] not in keys, 'shared-bytes', row_path + '.key')
        keys.add(row['key'])
        attributes = row.get('attributes') if row['kind'] == 'mesh' else []
        need(isinstance(attributes, list), 'schema', row_path + '.attributes')
        for a_index, attribute in enumerate(attributes):
            a_path = f'{row_path}.attributes[{a_index}]'
            fields(attribute, {'semantic', 'components', 'fourthComponent'}, a_path)
            need(attribute['semantic'] in ('POSITION', 'NORMAL') and attribute['components'] in (3, 4), 'schema', a_path)
            # A fourth component is a preserved custom scalar channel: no homogeneous divide, never dropped.
            expected = None if attribute['components'] == 3 else 'custom-scalar'
            need(attribute['fourthComponent'] == expected, 'vector-channel', a_path)
    return keys


def descriptor(value: Any, path: str, byte_keys: set[str]) -> list[str]:
    fields(value, {'reference', 'adapter', 'source', 'authorization', 'runtimeLocator', 'hierarchy',
                   'areas', 'connectors', *SPATIAL}, path)
    reference = fields(value['reference'], {'id', 'revision'}, path + '.reference')
    need(text(reference['id'], 512) and not re.search(r'[\s/\\]', reference['id'])
         and isinstance(reference['revision'], str) and HASH.fullmatch(reference['revision']) is not None,
         'reference', path + '.reference')
    adapter = fields(value['adapter'], {'profile', 'profileRevision'}, path + '.adapter')
    need(PROFILES.get(adapter['profile']) == adapter['profileRevision'], 'unsupported-profile', path + '.adapter')

    source = value['source']
    need(isinstance(source, dict) and source.get('kind') in ('package', 'native'), 'source', path + '.source.kind')
    authorization = fields(value['authorization'], {'reviewRef', 'runtimeUse', 'redistributeBytes', 'dependency'},
                           path + '.authorization')
    if source['kind'] == 'package':
        fields(source, {'kind', 'packagePin', 'archiveSha256', 'bundle', 'assetPath', 'object'}, path + '.source')
        need(isinstance(source['packagePin'], str) and PIN.fullmatch(source['packagePin']) is not None
             and isinstance(source['archiveSha256'], str) and HASH.fullmatch(source['archiveSha256']) is not None
             and text(source['bundle'], 512) and text(source['assetPath']), 'source', path + '.source')
        file, path_id = source_object(source['object'], path + '.source.object')
        need(reference['revision'] == source['archiveSha256'], 'revision-mismatch', path + '.reference.revision')
        need(reference['id'] == imported_id(source['packagePin'], source['bundle'], file, path_id),
             'reference-derivation', path + '.reference.id')
        need(authorization['runtimeUse'] == 'installed-package' and authorization['dependency'] == source['packagePin'],
             'authorization', path + '.authorization.dependency')
    else:
        fields(source, {'kind', 'sourceId', 'contentSha256', 'evidence', 'assetPath', 'object'}, path + '.source')
        evidence = fields(source['evidence'], {'path', 'sha256'}, path + '.source.evidence')
        need(text(source['sourceId'], 512) and text(source['assetPath']) and text(evidence['path'])
             and all(isinstance(h, str) and HASH.fullmatch(h) is not None
                     for h in (source['contentSha256'], evidence['sha256'])), 'source', path + '.source')
        file, path_id = source_object(source['object'], path + '.source.object')
        need(reference['revision'] == source['contentSha256'], 'revision-mismatch', path + '.reference.revision')
        need(authorization['runtimeUse'] == 'game-content' and authorization['dependency'] is None,
             'authorization', path + '.authorization.dependency')
    need(text(authorization['reviewRef'], 512) and authorization['redistributeBytes'] is False,
         'authorization', path + '.authorization')

    locator = fields(value['runtimeLocator'], {'loadedBy', 'assetPath', 'requiresLoaded'}, path + '.runtimeLocator')
    need(locator['loadedBy'] == 'complex-resource-set' and locator['assetPath'] == source['assetPath']
         and locator['requiresLoaded'] is True, 'runtime-locator', path + '.runtimeLocator')

    hierarchy = fields(value['hierarchy'], {'space', 'nodes'}, path + '.hierarchy')
    nodes = hierarchy['nodes']
    need(hierarchy['space'] == 'unity-left-handed-meters' and isinstance(nodes, list)
         and 0 < len(nodes) <= MAX_NODES, 'hierarchy', path + '.hierarchy')
    parents: dict[str, str | None] = {}
    root_sources: list[tuple[str, str]] = []
    for index, node in enumerate(nodes):
        n_path = f'{path}.hierarchy.nodes[{index}]'
        fields(node, {'id', 'parent', 'name', 'source', 'gameObject', 'local', 'active', 'meshes'}, n_path)
        node_source = source_object(node['source'], n_path + '.source')
        source_object(node['gameObject'], n_path + '.gameObject')
        need(node['id'] == node_source[0] + ':' + node_source[1], 'node-identity', n_path + '.id')
        need(node['id'] not in parents, 'duplicate-node', n_path + '.id')
        need(node['parent'] is None or text(node['parent'], 600), 'hierarchy', n_path + '.parent')
        need(text(node['name'], 512) and isinstance(node['active'], bool), 'schema', n_path)
        transform(node['local'], n_path + '.local')
        need(isinstance(node['meshes'], list) and len(set(node['meshes'])) == len(node['meshes']), 'schema', n_path + '.meshes')
        for key in node['meshes']:
            need(key in byte_keys, 'shared-bytes-missing', n_path + '.meshes: ' + str(key))
        parents[node['id']] = node['parent']
        if node['parent'] is None:
            root_sources.append(node_source)
    need(len(root_sources) == 1, 'hierarchy', path + ' requires exactly one root')
    for node_id, parent in parents.items():
        need(parent is None or parent in parents, 'hierarchy', path + ' unknown parent of ' + node_id)
    for node_id in parents:
        seen: set[str] = set()
        current: str | None = node_id
        while current is not None:
            need(current not in seen, 'hierarchy', path + ' cycle at ' + node_id)
            seen.add(current)
            current = parents[current]
    need(root_sources[0] == (file, path_id), 'root-mismatch', path + ' root node is not the locked source object')

    areas = value['areas']
    need(isinstance(areas, list), 'schema', path + '.areas')
    area_ids: set[str] = set()
    for index, area in enumerate(areas):
        a_path = f'{path}.areas[{index}]'
        fields(area, {'id', 'node'}, a_path)
        need(text(area['id'], 256) and area['id'] not in area_ids and area['node'] in parents, 'area', a_path)
        area_ids.add(area['id'])

    connectors = value['connectors']
    need(isinstance(connectors, list), 'schema', path + '.connectors')
    connector_ids: set[str] = set()
    for index, connector in enumerate(connectors):
        c_path = f'{path}.connectors[{index}]'
        fields(connector, {'id', 'node', 'area', 'expanderType', 'position', 'outward', 'doubleSided'}, c_path)
        need(text(connector['id'], 256) and connector['id'] not in connector_ids and connector['node'] in parents
             and connector['expanderType'] in ('plug', 'gate') and isinstance(connector['doubleSided'], bool)
             and vector(connector['position'], 3), 'connector', c_path)
        connector_ids.add(connector['id'])
        need(connector['area'] in area_ids, 'connector-area', c_path + '.area')
        need(vector(connector['outward'], 3) and abs(math.hypot(*connector['outward']) - 1) <= 1e-4,
             'connector-orientation', c_path + '.outward')

    blockers: list[str] = []
    for name in SPATIAL:
        statuses, item_fields = SPATIAL_RULES[name]
        block = value[name]
        s_path = path + '.' + name
        need(isinstance(block, dict), 'schema', s_path)
        # A navigation "ready" state is a runtime fact after the build; a static descriptor cannot assert it.
        need(block.get('status') in statuses, 'navigation-state' if name == 'navigation' else 'schema', s_path + '.status')
        fields(block, {'status', 'reason', 'items'}, s_path)
        need(isinstance(block['items'], list), 'schema', s_path + '.items')
        if block['status'] == 'unknown':
            need(text(block['reason'], 512) and not block['items'], 'unknown-reason', s_path)
            blockers.append(name)
            continue
        need(block['reason'] is None and block['items'], 'schema', s_path)
        for index, item in enumerate(block['items']):
            i_path = f'{s_path}.items[{index}]'
            fields(item, item_fields, i_path)
            need(item['node'] in parents, 'schema', i_path + '.node')
            if name == 'colliders':
                need(item['shape'] in ('box', 'sphere', 'capsule', 'mesh'), 'schema', i_path + '.shape')
                # Renderer or preview bounds are never collision evidence.
                need(item['evidence'] == 'collider-component', 'collider-evidence', i_path + '.evidence')
            elif name == 'navigation':
                need(item['kind'] in ('node-volume', 'navmesh-source'), 'schema', i_path + '.kind')
            else:
                need(item['kind'] in ('culling-portal', 'occluder') and item['area'] in area_ids, 'schema', i_path)
    return blockers


def document(value: Any, path: str = '$') -> list[dict[str, Any]]:
    """Validates one `forge-map-resource-descriptors` document and returns its per-descriptor blockers.

    `path` is the root path prefix of the emitted error paths. The standalone CLI uses `$`; the
    G0 assembly-plan checker imports this same function with `$descriptors` so both checkers stay
    one implementation (FORGE-FRAMEWORK.md section 3.2: descriptor-document paths start at
    `$descriptors`).
    """
    fields(value, {'schemaVersion', 'kind', 'evidence', 'sharedBytes', 'descriptors'}, path)
    need(value['schemaVersion'] == 1 and value['kind'] == 'forge-map-resource-descriptors', 'schema', path + '.kind')
    need(value['evidence'] in EVIDENCE, 'evidence-escalation', path + '.evidence')
    keys = shared_bytes(value['sharedBytes'], path + '.sharedBytes')
    rows = value['descriptors']
    need(isinstance(rows, list) and rows, 'schema', path + '.descriptors')
    results, identities = [], set()
    for index, row in enumerate(rows):
        blockers = descriptor(row, f'{path}.descriptors[{index}]', keys)
        identity = row['reference']['id'] + '@' + row['reference']['revision']
        # Shared bytes may be deduplicated; resource identity, authorization and instances never are.
        need(identity not in identities, 'duplicate-resource', f'{path}.descriptors[{index}].reference')
        identities.add(identity)
        results.append({'reference': identity, 'generationBlockers': blockers})
    return results


def main() -> int:
    module = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixtures', type=Path, default=module / 'tests/fixtures/resource-adapter')
    args = parser.parse_args()
    root = args.fixtures.resolve()
    cases = json.loads((root / 'cases.json').read_text(encoding='utf-8'))
    failures: list[str] = []
    listed = [row['file'] for row in cases['valid'] + cases['invalid']]
    on_disk = sorted(p.relative_to(root).as_posix() for p in root.rglob('*.json') if p.name != 'cases.json')
    if sorted(listed) != on_disk or len(set(listed)) != len(listed):
        failures.append('cases.json must list every fixture file exactly once')
    valid_passed = invalid_rejected = 0
    for row in cases['valid']:
        try:
            results = document(json.loads((root / row['file']).read_text(encoding='utf-8')))
            blockers = sorted({b for r in results for b in r['generationBlockers']})
            if blockers != sorted(row['expectedBlockers']):
                failures.append(f"{row['id']}: blockers {blockers} != {row['expectedBlockers']}")
            else:
                valid_passed += 1
        except DescriptorError as error:
            failures.append(f"{row['id']}: valid fixture rejected: {error}")
    for row in cases['invalid']:
        try:
            document(json.loads((root / row['file']).read_text(encoding='utf-8')))
            failures.append(f"{row['id']}: invalid fixture accepted")
        except DescriptorError as error:
            if error.code != row['expectedError']:
                failures.append(f"{row['id']}: expected {row['expectedError']}, got {error}")
            else:
                invalid_rejected += 1
    passed = not failures and valid_passed == len(cases['valid']) and invalid_rejected == len(cases['invalid'])
    print(json.dumps({'verification': 'fixture-schema-only', 'nativeGameExecuted': False,
                      'passed': passed, 'validPassed': valid_passed, 'invalidRejected': invalid_rejected,
                      'failures': failures}, indent=2))
    return 0 if passed else 1


if __name__ == '__main__':
    sys.exit(main())
