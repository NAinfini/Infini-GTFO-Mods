"""Check G0 map-assembly-plan fixtures against FORGE-FRAMEWORK.md section 3.2 (I-MAP-PLAN schemaVersion 1).

Static, fixture-schema evidence only: reads JSON, never loads a bundle, game assembly, Native/ discovery
path or installed package. A valid plan is not a generation success ("static checks passed" != "generation
succeeded"); v1 always yields the `dimension-bounds-unknown` blocker.

Reuses `verify_resource_adapter_fixtures.document(..., '$descriptors')` for the GENERATION-SPEC.md section
3.1 resource-descriptor document referenced by each plan case, so `descriptor.*` codes and paths are identical
to the standalone resource-adapter checker; the imported function takes the document path prefix as an
argument, and FORGE-FRAMEWORK.md section 3.2 roots descriptor-document paths at `$descriptors`.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import math
import re
import struct
import sys
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from verify_resource_adapter_fixtures import document, DescriptorError, HASH  # noqa: E402

ID = re.compile(r'[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}')
ROOM_ID = re.compile(r'forge\.native\.room:[a-z0-9][a-z0-9-]*')
DESCRIPTORS_PATH = 'forge/maps/rooms.descriptors.json'

# schemaVersion 1 static limits (FORGE-FRAMEWORK.md section 3.2); no per-plan budget field.
LIMITS = {'zones': 64, 'placements': 256, 'pairs': 512, 'perZonePlacements': 32, 'descriptors': 256}

# rotation[3] literal from JS `JSON.stringify(Math.fround(Math.SQRT1_2))`, per the canonical-quaternion table.
_HALF = 0.7071067690849304
CANON_ROTATIONS = ((0.0, 0.0, 0.0, 1.0), (0.0, -_HALF, 0.0, _HALF), (0.0, 1.0, 0.0, 0.0), (0.0, _HALF, 0.0, _HALF))

PLAN_FIELDS = {'schemaVersion', 'kind', 'planId', 'levelLayoutId', 'seed', 'descriptors', 'zones', 'entry',
               'placements', 'pairs'}
DESCRIPTORS_REF_FIELDS = {'path', 'sha256'}
ZONE_FIELDS = {'dimension', 'layer', 'localIndex', 'parentLocalIndex'}
ENTRY_FIELDS = {'placementId'}
PLACEMENT_FIELDS = {'placementId', 'room', 'locator', 'transform'}
ROOM_FIELDS = {'id', 'revision'}
LOCATOR_FIELDS = {'dimension', 'layer', 'localZoneIndex'}
TRANSFORM_FIELDS = {'position', 'rotation', 'scale'}
PAIR_FIELDS = {'pairId', 'a', 'b', 'door'}
ENDPOINT_FIELDS = {'placementId', 'connectorId'}
DOOR_FIELDS = {'doorId'}


class AssemblyError(Exception):
    def __init__(self, code: str, path: str) -> None:
        super().__init__(code + ': ' + path)
        self.code = code
        self.path = path


def fail(code: str, path: str) -> None:
    raise AssemblyError('assembly.' + code, path)


def need(ok: bool, code: str, path: str) -> None:
    if not ok:
        fail(code, path)


def load_document(descriptors_bytes: bytes) -> Any:
    """Parses the descriptor-document bytes a plan locks; unparsable bytes fail as descriptor.schema."""
    try:
        return json.loads(descriptors_bytes.decode('utf-8'))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise AssemblyError('descriptor.schema', '$descriptors') from None


def is_int(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def is_number(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def fround(value: float) -> float:
    # JSON integers are arbitrary precision in Python; an int too large for float32 must reject, not raise.
    try:
        return struct.unpack('<f', struct.pack('<f', value))[0]
    except OverflowError:
        return math.inf


def norm_zero(value: float) -> float:
    return 0.0 if value == 0 else float(value)


def fields(value: Any, allowed: set[str], path: str) -> dict[str, Any]:
    need(isinstance(value, dict), 'schema', path + ' must be an object')
    need(set(value) <= allowed, 'unknown-field', path + ': ' + ', '.join(sorted(set(value) - allowed)))
    need(set(value) == allowed, 'schema', path + ' missing ' + ', '.join(sorted(allowed - set(value))))
    return value


def numeric_vector(value: Any, size: int, path: str) -> None:
    need(isinstance(value, list) and len(value) == size and all(is_number(n) for n in value), 'schema', path)


# ---------------------------------------------------------------------------
# Phase 2-3 (codes assembly.schema / assembly.unknown-field): one top-down,
# left-to-right, index-ordered pass over the whole document. No later (value,
# range, pattern, cross-reference) check runs until this entire pass succeeds.
# ---------------------------------------------------------------------------

def validate_shape(plan: Any) -> None:
    fields(plan, PLAN_FIELDS, '$')
    need(plan['kind'] == 'forge-map-assembly-plan' and plan['schemaVersion'] == 1, 'schema', '$.kind')
    need(isinstance(plan['planId'], str), 'schema', '$.planId')
    need(is_int(plan['levelLayoutId']), 'schema', '$.levelLayoutId')
    need(is_int(plan['seed']), 'schema', '$.seed')
    descriptors_ref = fields(plan['descriptors'], DESCRIPTORS_REF_FIELDS, '$.descriptors')
    need(isinstance(descriptors_ref['path'], str), 'schema', '$.descriptors.path')
    need(isinstance(descriptors_ref['sha256'], str), 'schema', '$.descriptors.sha256')

    need(isinstance(plan['zones'], list), 'schema', '$.zones')
    for index, zone in enumerate(plan['zones']):
        path = f'$.zones[{index}]'
        fields(zone, ZONE_FIELDS, path)
        need(is_int(zone['dimension']), 'schema', path + '.dimension')
        need(is_int(zone['layer']), 'schema', path + '.layer')
        need(is_int(zone['localIndex']), 'schema', path + '.localIndex')
        need(zone['parentLocalIndex'] is None or is_int(zone['parentLocalIndex']), 'schema', path + '.parentLocalIndex')

    entry = fields(plan['entry'], ENTRY_FIELDS, '$.entry')
    need(isinstance(entry['placementId'], str), 'schema', '$.entry.placementId')

    need(isinstance(plan['placements'], list), 'schema', '$.placements')
    for index, placement in enumerate(plan['placements']):
        path = f'$.placements[{index}]'
        fields(placement, PLACEMENT_FIELDS, path)
        need(isinstance(placement['placementId'], str), 'schema', path + '.placementId')
        room = fields(placement['room'], ROOM_FIELDS, path + '.room')
        need(isinstance(room['id'], str), 'schema', path + '.room.id')
        need(isinstance(room['revision'], str), 'schema', path + '.room.revision')
        locator = fields(placement['locator'], LOCATOR_FIELDS, path + '.locator')
        need(is_int(locator['dimension']), 'schema', path + '.locator.dimension')
        need(is_int(locator['layer']), 'schema', path + '.locator.layer')
        need(is_int(locator['localZoneIndex']), 'schema', path + '.locator.localZoneIndex')
        transform = fields(placement['transform'], TRANSFORM_FIELDS, path + '.transform')
        numeric_vector(transform['position'], 3, path + '.transform.position')
        numeric_vector(transform['rotation'], 4, path + '.transform.rotation')
        numeric_vector(transform['scale'], 3, path + '.transform.scale')

    need(isinstance(plan['pairs'], list), 'schema', '$.pairs')
    for index, pair in enumerate(plan['pairs']):
        path = f'$.pairs[{index}]'
        fields(pair, PAIR_FIELDS, path)
        need(isinstance(pair['pairId'], str), 'schema', path + '.pairId')
        for side in ('a', 'b'):
            endpoint = fields(pair[side], ENDPOINT_FIELDS, f'{path}.{side}')
            need(isinstance(endpoint['placementId'], str), 'schema', f'{path}.{side}.placementId')
            need(isinstance(endpoint['connectorId'], str), 'schema', f'{path}.{side}.connectorId')
        door = pair['door']
        need(door is None or isinstance(door, dict), 'schema', path + '.door')
        if door is not None:
            fields(door, DOOR_FIELDS, path + '.door')
            need(isinstance(door['doorId'], str) and len(door['doorId']) > 0, 'schema', path + '.door.doorId')


def zone_key(dimension: int, layer: int, index: int) -> tuple[int, int, int]:
    return (dimension, layer, index)


def locator_key(locator: dict[str, Any]) -> tuple[int, int, int]:
    return (locator['dimension'], locator['layer'], locator['localZoneIndex'])


def world_point(placement: dict[str, Any], local: tuple[float, float, float]) -> tuple[float, float, float]:
    rx, ry, rz = _rotate(placement['transform']['rotation'], local)
    px, py, pz = placement['transform']['position']
    return (rx + px, ry + py, rz + pz)


def world_direction(placement: dict[str, Any], local: tuple[float, float, float]) -> tuple[float, float, float]:
    return _rotate(placement['transform']['rotation'], local)


def _rotate(rotation: list[float], v: tuple[float, float, float]) -> tuple[float, float, float]:
    x, y, z, w = (float(c) for c in rotation)
    vx, vy, vz = (float(c) for c in v)
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))


def validate_plan(plan: Any, descriptors_doc: Any, descriptors_bytes: bytes) -> list[str]:
    """Runs the full FORGE-FRAMEWORK.md section 3.2 order (#1 descriptor.* .. #34 zone-disconnected).

    Raises AssemblyError(code, path) on the first violation; array checks walk arrays by index.
    Returns the deduplicated, sorted blocker list on success.
    """
    # 1. descriptor.* (delegates to GENERATION-SPEC.md section 3.1; paths rooted at $descriptors).
    try:
        doc_results = document(descriptors_doc, '$descriptors')
    except DescriptorError as error:
        detail = str(error)[len(error.code) + 2:]
        raise AssemblyError(error.code, detail) from None
    blocker_map = {row['reference']: row['generationBlockers'] for row in doc_results}
    descriptors_by_ref = {(row['reference']['id'], row['reference']['revision']): row
                           for row in descriptors_doc['descriptors']}

    # 2-3. assembly.schema / assembly.unknown-field (whole document).
    validate_shape(plan)

    # 4-6. simple scalar fields.
    need(ID.fullmatch(plan['planId']) is not None, 'plan-id', '$.planId')
    need(1 <= plan['levelLayoutId'] <= 4294967295, 'level-layout', '$.levelLayoutId')
    need(0 <= plan['seed'] <= 2147483647, 'seed', '$.seed')

    # 7. assembly.descriptor-lock.
    descriptors_ref = plan['descriptors']
    actual_sha = hashlib.sha256(descriptors_bytes).hexdigest()
    need(descriptors_ref['path'] == DESCRIPTORS_PATH, 'descriptor-lock', '$.descriptors.path')
    need(descriptors_ref['sha256'] == actual_sha, 'descriptor-lock', '$.descriptors.sha256')

    # 8. assembly.limit.
    need(len(plan['zones']) <= LIMITS['zones'], 'limit', '$.zones')
    need(len(plan['placements']) <= LIMITS['placements'], 'limit', '$.placements')
    need(len(plan['pairs']) <= LIMITS['pairs'], 'limit', '$.pairs')
    need(len(descriptors_doc['descriptors']) <= LIMITS['descriptors'], 'limit', '$descriptors.descriptors')
    zone_counts: dict[tuple[int, int, int], int] = {}
    for placement in plan['placements']:
        key = locator_key(placement['locator'])
        zone_counts[key] = zone_counts.get(key, 0) + 1
    need(all(count <= LIMITS['perZonePlacements'] for count in zone_counts.values()), 'limit', '$.placements')

    # 9. assembly.order.
    zones = plan['zones']
    need(zones == sorted(zones, key=lambda z: (z['dimension'], z['layer'], z['localIndex'])), 'order', '$.zones')
    placements = plan['placements']
    need(placements == sorted(placements, key=lambda p: (*locator_key(p['locator']), p['placementId'])),
         'order', '$.placements')
    pairs = plan['pairs']
    need(pairs == sorted(pairs, key=lambda pr: pr['pairId']), 'order', '$.pairs')
    raw_locator_by_id = {p['placementId']: p['locator'] for p in placements}
    for index, pair in enumerate(pairs):
        a_locator = raw_locator_by_id.get(pair['a']['placementId'])
        b_locator = raw_locator_by_id.get(pair['b']['placementId'])
        if a_locator is None or b_locator is None or locator_key(a_locator) != locator_key(b_locator):
            continue  # cross-zone or dangling; resolved by pair-direction/connector-reference below.
        a_key = (pair['a']['placementId'], pair['a']['connectorId'])
        b_key = (pair['b']['placementId'], pair['b']['connectorId'])
        need(a_key <= b_key, 'order', f'$.pairs[{index}]')

    # 10. assembly.zone.
    for index, zone in enumerate(zones):
        path = f'$.zones[{index}]'
        need(zone['dimension'] == 0 and zone['layer'] == 0, 'zone', path)
    local_indices = [z['localIndex'] for z in zones]
    need(sorted(local_indices) == list(range(len(zones))), 'zone', '$.zones')
    for index, zone in enumerate(zones):
        path = f'$.zones[{index}]'
        if zone['localIndex'] == 0:
            need(zone['parentLocalIndex'] is None, 'zone', path + '.parentLocalIndex')
        else:
            need(zone['parentLocalIndex'] is not None and 0 <= zone['parentLocalIndex'] < zone['localIndex'],
                 'zone', path + '.parentLocalIndex')

    # 11-12. placement-id / duplicate-placement.
    for index, placement in enumerate(placements):
        need(ID.fullmatch(placement['placementId']) is not None, 'placement-id', f'$.placements[{index}].placementId')
    seen_placement_ids: set[str] = set()
    for index, placement in enumerate(placements):
        need(placement['placementId'] not in seen_placement_ids, 'duplicate-placement', f'$.placements[{index}].placementId')
        seen_placement_ids.add(placement['placementId'])

    # 13-14. room-reference / room-descriptor.
    for index, placement in enumerate(placements):
        room = placement['room']
        path = f'$.placements[{index}].room'
        need(ROOM_ID.fullmatch(room['id']) is not None, 'room-reference', path + '.id')
        need(HASH.fullmatch(room['revision']) is not None, 'room-reference', path + '.revision')
    for index, placement in enumerate(placements):
        room = placement['room']
        need((room['id'], room['revision']) in descriptors_by_ref, 'room-descriptor', f'$.placements[{index}].room')

    # 15-16. locator / locator-unsupported.
    zones_set = {zone_key(z['dimension'], z['layer'], z['localIndex']) for z in zones}
    for index, placement in enumerate(placements):
        locator = placement['locator']
        path = f'$.placements[{index}].locator'
        need(locator['dimension'] >= 0 and locator['layer'] >= 0 and locator['localZoneIndex'] >= 0, 'locator', path)
        need(locator_key(locator) in zones_set, 'locator', path)
    for index, placement in enumerate(placements):
        locator = placement['locator']
        need(locator['dimension'] == 0 and locator['layer'] == 0, 'locator-unsupported', f'$.placements[{index}].locator')

    # 17. assembly.float32.
    for index, placement in enumerate(placements):
        transform = placement['transform']
        for field_name in ('position', 'rotation', 'scale'):
            path = f'$.placements[{index}].transform.{field_name}'
            need(all(math.isfinite(n) and fround(n) == n for n in transform[field_name]), 'float32', path)

    # 18-19. transform-rotation / transform-scale.
    for index, placement in enumerate(placements):
        rotation = tuple(norm_zero(c) for c in placement['transform']['rotation'])
        need(rotation in CANON_ROTATIONS, 'transform-rotation', f'$.placements[{index}].transform.rotation')
    for index, placement in enumerate(placements):
        scale = tuple(norm_zero(c) for c in placement['transform']['scale'])
        need(scale == (1.0, 1.0, 1.0), 'transform-scale', f'$.placements[{index}].transform.scale')

    # 20-21. entry / entry-transform.
    entry_placement_id = plan['entry']['placementId']
    need(entry_placement_id in seen_placement_ids, 'entry', '$.entry.placementId')
    entry_placement = next(p for p in placements if p['placementId'] == entry_placement_id)
    need(entry_placement['locator']['localZoneIndex'] == 0, 'entry', '$.entry.placementId')
    entry_transform = entry_placement['transform']
    entry_ok = (tuple(norm_zero(c) for c in entry_transform['position']) == (0.0, 0.0, 0.0)
                and tuple(norm_zero(c) for c in entry_transform['rotation']) == (0.0, 0.0, 0.0, 1.0)
                and tuple(norm_zero(c) for c in entry_transform['scale']) == (1.0, 1.0, 1.0))
    need(entry_ok, 'entry-transform', '$.entry.placementId')

    # 22. assembly.zone-empty.
    for index, zone in enumerate(zones):
        key = zone_key(zone['dimension'], zone['layer'], zone['localIndex'])
        count = sum(1 for p in placements if locator_key(p['locator']) == key)
        need(count >= 1, 'zone-empty', f'$.zones[{index}]')

    # 23-24. pair-id / duplicate-pair.
    for index, pair in enumerate(pairs):
        need(ID.fullmatch(pair['pairId']) is not None, 'pair-id', f'$.pairs[{index}].pairId')
    seen_pair_ids: set[str] = set()
    for index, pair in enumerate(pairs):
        need(pair['pairId'] not in seen_pair_ids, 'duplicate-pair', f'$.pairs[{index}].pairId')
        seen_pair_ids.add(pair['pairId'])

    placements_by_id = {p['placementId']: p for p in placements}

    def connector_of(placement: dict[str, Any], connector_id: str) -> dict[str, Any] | None:
        descriptor = descriptors_by_ref[(placement['room']['id'], placement['room']['revision'])]
        for connector in descriptor['connectors']:
            if connector['id'] == connector_id:
                return connector
        return None

    # 25. assembly.connector-reference.
    for index, pair in enumerate(pairs):
        for side in ('a', 'b'):
            endpoint = pair[side]
            path = f'$.pairs[{index}].{side}'
            need(endpoint['placementId'] in placements_by_id, 'connector-reference', path + '.placementId')
            placement = placements_by_id[endpoint['placementId']]
            need(connector_of(placement, endpoint['connectorId']) is not None, 'connector-reference', path + '.connectorId')

    # 26. assembly.connector-type.
    for index, pair in enumerate(pairs):
        for side in ('a', 'b'):
            endpoint = pair[side]
            placement = placements_by_id[endpoint['placementId']]
            connector = connector_of(placement, endpoint['connectorId'])
            need(connector['expanderType'] == 'plug', 'connector-type', f'$.pairs[{index}].{side}.connectorId')

    # 27. assembly.connector-self.
    for index, pair in enumerate(pairs):
        need(pair['a']['placementId'] != pair['b']['placementId'], 'connector-self', f'$.pairs[{index}]')

    # 28. assembly.connector-reused.
    seen_endpoints: set[tuple[str, str]] = set()
    for index, pair in enumerate(pairs):
        for side in ('a', 'b'):
            endpoint = pair[side]
            key = (endpoint['placementId'], endpoint['connectorId'])
            need(key not in seen_endpoints, 'connector-reused', f'$.pairs[{index}].{side}')
            seen_endpoints.add(key)

    def zone_of(placement: dict[str, Any]) -> tuple[int, int, int]:
        return locator_key(placement['locator'])

    zone_by_key = {zone_key(z['dimension'], z['layer'], z['localIndex']): z for z in zones}

    # 29. assembly.pair-direction.
    for index, pair in enumerate(pairs):
        zone_a = zone_of(placements_by_id[pair['a']['placementId']])
        zone_b = zone_of(placements_by_id[pair['b']['placementId']])
        if zone_a != zone_b:
            need(zone_by_key[zone_b]['parentLocalIndex'] == zone_a[2], 'pair-direction', f'$.pairs[{index}]')

    # 30. assembly.door.
    for index, pair in enumerate(pairs):
        zone_a = zone_of(placements_by_id[pair['a']['placementId']])
        zone_b = zone_of(placements_by_id[pair['b']['placementId']])
        path = f'$.pairs[{index}].door'
        if zone_a != zone_b:
            need(pair['door'] is not None, 'door', path)
        else:
            need(pair['door'] is None, 'door', path)

    # 31-32. connector-orientation / connector-position (double math on float32 inputs).
    for index, pair in enumerate(pairs):
        placement_a = placements_by_id[pair['a']['placementId']]
        placement_b = placements_by_id[pair['b']['placementId']]
        connector_a = connector_of(placement_a, pair['a']['connectorId'])
        connector_b = connector_of(placement_b, pair['b']['connectorId'])
        world_a = world_direction(placement_a, tuple(connector_a['outward']))
        world_b = world_direction(placement_b, tuple(connector_b['outward']))
        norm_a = math.sqrt(sum(c * c for c in world_a)) or 1.0
        norm_b = math.sqrt(sum(c * c for c in world_b)) or 1.0
        dot = sum((a / norm_a) * (b / norm_b) for a, b in zip(world_a, world_b))
        need(dot <= -0.9999, 'connector-orientation', f'$.pairs[{index}]')
    for index, pair in enumerate(pairs):
        placement_a = placements_by_id[pair['a']['placementId']]
        placement_b = placements_by_id[pair['b']['placementId']]
        connector_a = connector_of(placement_a, pair['a']['connectorId'])
        connector_b = connector_of(placement_b, pair['b']['connectorId'])
        point_a = world_point(placement_a, tuple(connector_a['position']))
        point_b = world_point(placement_b, tuple(connector_b['position']))
        distance = math.sqrt(sum((a - b) ** 2 for a, b in zip(point_a, point_b)))
        need(distance <= 1e-3, 'connector-position', f'$.pairs[{index}]')

    # 33. assembly.zone-link.
    for index, zone in enumerate(zones):
        if zone['localIndex'] == 0:
            continue
        key = zone_key(zone['dimension'], zone['layer'], zone['localIndex'])
        count = 0
        for pair in pairs:
            zone_a = zone_of(placements_by_id[pair['a']['placementId']])
            zone_b = zone_of(placements_by_id[pair['b']['placementId']])
            if zone_a != zone_b and zone_b == key:
                count += 1
        need(count == 1, 'zone-link', f'$.zones[{index}]')

    # 34. assembly.zone-disconnected.
    for index, zone in enumerate(zones):
        key = zone_key(zone['dimension'], zone['layer'], zone['localIndex'])
        members = [p['placementId'] for p in placements if zone_of(p) == key]
        if len(members) <= 1:
            continue
        adjacency: dict[str, set[str]] = {m: set() for m in members}
        for pair in pairs:
            zone_a = zone_of(placements_by_id[pair['a']['placementId']])
            zone_b = zone_of(placements_by_id[pair['b']['placementId']])
            if zone_a == key and zone_b == key:
                adjacency[pair['a']['placementId']].add(pair['b']['placementId'])
                adjacency[pair['b']['placementId']].add(pair['a']['placementId'])
        visited = {members[0]}
        frontier = [members[0]]
        while frontier:
            current = frontier.pop()
            for neighbour in adjacency[current]:
                if neighbour not in visited:
                    visited.add(neighbour)
                    frontier.append(neighbour)
        need(len(visited) == len(members), 'zone-disconnected', f'$.zones[{index}]')

    referenced = {(p['room']['id'], p['room']['revision']) for p in placements}
    blockers = {'dimension-bounds-unknown'}
    for room_id, revision in referenced:
        blockers.update(blocker_map.get(room_id + '@' + revision, []))
    return sorted(blockers)


# ---------------------------------------------------------------------------
# Package-level checks (assembly.plan-file / duplicate-level-layout / package-layout).
# Runs at "model discovery" scope: a `root` directory standing in for `BepInEx/plugins/`.
# ---------------------------------------------------------------------------

def check_package(root: Path) -> list[str]:
    plugin_dirs = [d for d in sorted(root.iterdir()) if d.is_dir()]
    map_dirs = [d for d in plugin_dirs if (d / 'forge' / 'maps').is_dir()]
    need(len(map_dirs) == 1, 'package-layout', str(root))
    maps_dir = map_dirs[0] / 'forge' / 'maps'
    descriptors_path = maps_dir / DESCRIPTORS_PATH.split('/')[-1]
    need(descriptors_path.is_file(), 'package-layout', str(maps_dir))
    descriptors_bytes = descriptors_path.read_bytes()
    descriptors_doc = load_document(descriptors_bytes)
    plan_files = sorted(maps_dir.glob('*.assembly.json'))
    seen_layouts: dict[int, str] = {}
    all_blockers: set[str] = set()
    for plan_file in plan_files:
        plan = json.loads(plan_file.read_text('utf-8'))
        plan_id = plan.get('planId') if isinstance(plan, dict) else None
        need(isinstance(plan_id, str) and plan_file.name == plan_id + '.assembly.json', 'plan-file', str(plan_file))
        level = plan.get('levelLayoutId') if isinstance(plan, dict) else None
        need(level not in seen_layouts, 'duplicate-level-layout', str(plan_file))
        seen_layouts[level] = plan_file.name
        all_blockers.update(validate_plan(plan, descriptors_doc, descriptors_bytes))
    return sorted(all_blockers)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixtures', type=Path, required=True,
                         help='Tests/Forge/fixtures/map-assembly checked out from the website repo')
    args = parser.parse_args()
    root = args.fixtures.resolve()
    failures: list[str] = []

    # An incomplete fixture set is a fixture failure, not a crash: the website export (U-MAP-WEB) may not
    # have produced it yet.
    if not (root / 'MANIFEST.json').is_file() or not (root / 'cases.json').is_file():
        print(json.dumps({'verification': 'assembly-plan-fixture-only', 'nativeGameExecuted': False,
                          'manifestVerified': False, 'passed': False,
                          'failures': [f'{root} must contain MANIFEST.json and cases.json']}, indent=2))
        return 1

    manifest = json.loads((root / 'MANIFEST.json').read_text('utf-8'))
    listed = {row['path']: row['sha256'] for row in manifest['files']}
    on_disk = sorted(p.relative_to(root).as_posix() for p in root.rglob('*')
                      if p.is_file() and p.name != 'MANIFEST.json')
    manifest_verified = False
    if manifest.get('schemaVersion') != 1 or sorted(listed) != on_disk or len(listed) != len(manifest['files']):
        failures.append('MANIFEST.json must list every fixture file exactly once')
    else:
        mismatches = [path for path, expected in listed.items()
                      if hashlib.sha256((root / path).read_bytes()).hexdigest() != expected]
        if mismatches:
            failures.append('MANIFEST.json sha256 mismatch: ' + ', '.join(mismatches))
        else:
            manifest_verified = True

    cases = json.loads((root / 'cases.json').read_text('utf-8'))
    valid_passed = invalid_rejected = package_passed = 0
    for row in cases.get('valid', []):
        try:
            plan = json.loads((root / row['file']).read_text('utf-8'))
            descriptors_bytes = (root / row['descriptors']).read_bytes()
            descriptors_doc = load_document(descriptors_bytes)
            blockers = validate_plan(plan, descriptors_doc, descriptors_bytes)
            if sorted(blockers) != sorted(row['expectedBlockers']):
                failures.append(f"{row['id']}: blockers {blockers} != {row['expectedBlockers']}")
            else:
                valid_passed += 1
        except AssemblyError as error:
            failures.append(f"{row['id']}: valid fixture rejected: {error.code} {error.path}")
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            failures.append(f"{row['id']}: fixture is not readable JSON: {error}")
    for row in cases.get('invalid', []):
        try:
            plan = json.loads((root / row['file']).read_text('utf-8'))
            descriptors_bytes = (root / row['descriptors']).read_bytes()
            descriptors_doc = load_document(descriptors_bytes)
            validate_plan(plan, descriptors_doc, descriptors_bytes)
            failures.append(f"{row['id']}: invalid fixture accepted")
        except AssemblyError as error:
            expected = row['expectedError']
            if error.code != expected['code'] or error.path != expected['path']:
                failures.append(f"{row['id']}: expected {expected}, got "
                                 f"{{'code': '{error.code}', 'path': '{error.path}'}}")
            else:
                invalid_rejected += 1
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            failures.append(f"{row['id']}: fixture is not readable JSON: {error}")
    for row in cases.get('package', []):
        try:
            blockers = check_package(root / row['root'])
            if 'expectedError' in row:
                failures.append(f"{row['id']}: package fixture accepted")
            elif sorted(blockers) != sorted(row['expectedBlockers']):
                failures.append(f"{row['id']}: blockers {blockers} != {row['expectedBlockers']}")
            else:
                package_passed += 1
        except AssemblyError as error:
            if 'expectedError' not in row:
                failures.append(f"{row['id']}: package fixture rejected: {error.code} {error.path}")
                continue
            expected = row['expectedError']
            if error.code != expected['code'] or (expected.get('path') is not None and error.path != expected['path']):
                failures.append(f"{row['id']}: expected {expected}, got "
                                 f"{{'code': '{error.code}', 'path': '{error.path}'}}")
            else:
                package_passed += 1
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            failures.append(f"{row['id']}: package fixture is not readable JSON: {error}")

    counts = {
        'valid': {'passed': valid_passed, 'total': len(cases.get('valid', []))},
        'invalid': {'rejected': invalid_rejected, 'total': len(cases.get('invalid', []))},
        'package': {'passed': package_passed, 'total': len(cases.get('package', []))},
    }
    passed = (not failures and manifest_verified
              and valid_passed == counts['valid']['total']
              and invalid_rejected == counts['invalid']['total']
              and package_passed == counts['package']['total'])
    print(json.dumps({'verification': 'assembly-plan-fixture-only', 'nativeGameExecuted': False,
                      'manifestVerified': manifest_verified, 'passed': passed, 'counts': counts,
                      'failures': failures}, indent=2))
    return 0 if passed else 1


if __name__ == '__main__':
    sys.exit(main())
