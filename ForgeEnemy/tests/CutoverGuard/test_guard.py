"""Exercise the real read-only cutover CLI, including process exit status."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / 'ForgeEnemy/Native/scripts/check_cutover.py'
TARGET = 'ForgeEnemy/ModuleDefinition.cs'


class GuardTests(unittest.TestCase):
    def row(self, path=TARGET, before=None, after=None):
        digest = hashlib.sha256((ROOT / TARGET).read_bytes()).hexdigest()
        return {'path': path, 'beforeSha256': before or digest,
                'afterSha256': after or digest}

    def invoke(self, data, expected, mode='before'):
        original = (ROOT / TARGET).read_bytes()
        with tempfile.TemporaryDirectory(prefix='forge-cutover-guard-') as temp:
            manifest = Path(temp) / 'input.json'
            manifest.write_text(json.dumps(data), encoding='utf-8')
            result = subprocess.run([sys.executable, str(SCRIPT), str(manifest),
                '--mode', mode], capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
            self.assertEqual((ROOT / TARGET).read_bytes(), original)
    def check_rows(self, rows, expected, mode='before'):
        self.invoke({'schemaVersion': 1, 'changes': rows}, expected, mode)

    def test_matching_before(self):
        self.check_rows([self.row()], 0)

    def test_matching_after(self):
        self.check_rows([self.row()], 0, 'after')

    def test_stale_before(self):
        self.check_rows([self.row(before='0' * 64)], 1)

    def test_stale_after(self):
        self.check_rows([self.row(after='0' * 64)], 1, 'after')

    def test_missing_expected_file(self):
        self.check_rows([self.row(path='ForgeEnemy/absent-cutover-test.cs')], 1)

    def test_missing_expected_absent(self):
        row = self.row(path='ForgeEnemy/absent-cutover-test.cs')
        row['beforeSha256'] = None
        self.check_rows([row], 0)

    def test_existing_expected_absent(self):
        row = self.row(); row['beforeSha256'] = None
        self.check_rows([row], 1)

    def test_traversal(self):
        self.check_rows([self.row(path='../outside.cs')], 2)

    def test_absolute_path(self):
        self.check_rows([self.row(path='C:/outside.cs')], 2)

    def test_outside_selected_modules(self):
        self.check_rows([self.row(path='InfiniTweaks/Plugin.cs')], 2)

    def test_duplicate(self):
        self.check_rows([self.row(), self.row()], 2)

    def test_invalid_digest(self):
        self.check_rows([self.row(before='not-a-hash')], 2)

    def test_directory_not_file(self):
        self.check_rows([self.row(path='ForgeEnemy/Native')], 2)

    def test_empty_manifest(self):
        self.check_rows([], 2)

    def test_unknown_schema(self):
        self.invoke({'schemaVersion': 999, 'changes': [self.row()]}, 2)

    def test_missing_required_hash(self):
        row = self.row(); del row['beforeSha256']
        self.check_rows([row], 2)


if __name__ == '__main__':
    unittest.main()
