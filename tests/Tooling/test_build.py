"""Cargo diagnostics must not corrupt the machine-readable dependency inventory."""
import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'eng'))
import build


class BuildOutputTests(unittest.TestCase):
    def test_json_stdout_remains_parseable_when_child_writes_diagnostics(self):
        with tempfile.TemporaryDirectory() as directory:
            logs = Path(directory)
            with patch.object(build, 'LOGS', logs), contextlib.redirect_stdout(io.StringIO()):
                output = build.run([
                    sys.executable, '-c',
                    'import sys; print(\'{"packages": []}\'); print("warning: cargo diagnostic", file=sys.stderr)',
                ], 'metadata', json_stdout=True)
            self.assertEqual({'packages': []}, json.loads(output))
            self.assertIn('warning: cargo diagnostic', (logs / 'metadata.log').read_text())

    def test_json_command_failure_retains_stderr_and_is_not_accepted(self):
        with tempfile.TemporaryDirectory() as directory:
            logs = Path(directory)
            with patch.object(build, 'LOGS', logs), contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaisesRegex(RuntimeError, 'metadata failed \\(7\\)'):
                    build.run([
                        sys.executable, '-c',
                        'import sys; print("dependency resolution failed", file=sys.stderr); sys.exit(7)',
                    ], 'metadata', json_stdout=True)
            self.assertIn('dependency resolution failed', (logs / 'metadata.log').read_text())


if __name__ == '__main__':
    unittest.main()
