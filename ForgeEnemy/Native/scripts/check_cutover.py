"""Read-only guard for an E1 patch. This command never applies changes."""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json

ROOT = Path(__file__).resolve().parents[3]

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('manifest', type=Path)
    parser.add_argument('--mode', choices=['before', 'after'], default='before')
    args = parser.parse_args()
    data = json.loads(args.manifest.read_text(encoding='utf-8'))
    if data.get('schemaVersion') != 1 or not data.get('changes'):
        raise ValueError('Invalid or empty cutover input manifest.')
    failures = []
    seen = set()
    for entry in data['changes']:
        relative = PurePosixPath(entry['path'])
        if relative.is_absolute() or '..' in relative.parts or '\\' in str(relative) or ':' in str(relative):
            raise ValueError('Invalid relative patch path.')
        if str(relative) in seen or not relative.parts or relative.parts[0] not in {'ForgeEnemy', 'ForgeRuntime'}:
            raise ValueError('Duplicate or out-of-scope patch path.')
        seen.add(str(relative))
        path = ROOT / str(relative)
        if ROOT not in path.resolve().parents:
            raise ValueError('Patch path resolves outside the repository.')
        expected = entry[args.mode + 'Sha256']
        if expected is not None and (not isinstance(expected, str) or len(expected) != 64
                or any(c not in '0123456789abcdefABCDEF' for c in expected)):
            raise ValueError('Invalid expected SHA-256.')
        if expected is not None:
            expected = expected.lower()
        if path.exists() and not path.is_file():
            raise ValueError('Expected file or missing file, not a directory.')
        actual = hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None
        if actual != expected:
            failures.append(str(relative))
    print(json.dumps({'mode': args.mode, 'checked': len(seen),
                      'passed': not failures, 'mismatches': failures}, indent=2))
    return 1 if failures else 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, TypeError) as error:
        import sys
        print(f'Cutover check failed: {error}', file=sys.stderr)
        raise SystemExit(2)
