"""Regressions for the observed replay mismatch, evidence loss and stuck cleanup.

HTTP fixtures verify controller classification, not product correctness. Process
fixtures exercise real Linux child cleanup without database or performance load.
"""
import asyncio
import base64
import contextvars
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock
from unittest.mock import patch

import httpx

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'src/Benchmarks.Domain'
sys.path.insert(0, str(SOURCE))
from common import expected_value, load, load_a21, save
from http_target import IntegrityFailure, Target
from supervision import identity, stop_requested, supervised_command

spec = importlib.util.spec_from_file_location('b23_failure_controller', SOURCE / 'run.py')
controller = importlib.util.module_from_spec(spec)
spec.loader.exec_module(controller)


def target_fixture(directory):
    target = Target.__new__(Target)
    target.directory = directory
    target.a21 = SimpleNamespace(time=time)
    target.manifest = {'protocol': 'http1'}
    target.profile = {}
    target.protocols, target.http_outcomes, target.http_error_codes = set(), {}, {}
    target.failure_capture_count = target.failure_capture_omitted = target.error_capture_count = 0
    target.active_identifier = contextvars.ContextVar('test-logical-id', default=9)
    target.last_response = contextvars.ContextVar('test-last-response', default=None)
    target.prefix = 'fixture-'
    target.errors, target.mutations = [], []
    return target


class FailedResponses(unittest.IsolatedAsyncioTestCase):
    async def stream_fixture(self, directory, handler):
        target = target_fixture(directory)
        item_id, operation_id = '00000000-0000-4000-8000-000000000001', '00000000-0000-4000-8000-000000000002'
        item = {'kind': 'stream', 'seed': 41, 'bytes': 131072, 'sha256': hashlib.sha256(b'x' * 131072).hexdigest()}
        target.create = AsyncMock(return_value=(httpx.Response(201, content=b'create'), {'itemId': item_id}))
        put = httpx.Response(200, json={'operationId': operation_id, 'itemId': item_id,
            'responseVersion': 1, 'version': 2, 'contentHash': item['sha256'], 'contentLength': item['bytes']},
            headers={'x-source': 'previous-put'})
        target.request = AsyncMock(return_value=put)
        target.last_response.set((put, 'upload', 'PUT', '/v1/items/' + item_id + '/attachment'))
        target.golden = SimpleNamespace(scope_hash=lambda *args: 'scope', attachment_hash=lambda *args: 'hash')
        async with httpx.AsyncClient(base_url='http://fixture', transport=httpx.MockTransport(handler)) as client:
            target.client = client
            try:
                await target.operation(9, item)
            except Exception as error:
                target.capture_error(9, error)
            else:
                self.fail('The failed stream was reported as a successful operation')
        path = directory / 'http-failures.jsonl'
        return target, [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    async def test_wrong_download_protocol_captures_get_without_prior_put(self):
        with tempfile.TemporaryDirectory() as temporary:
            _, rows = await self.stream_fixture(Path(temporary), lambda request:
                httpx.Response(200, content=b'unread body', headers={'x-source': 'actual-get'},
                               extensions={'http_version': b'HTTP/2'}))
            self.assertEqual(1, len(rows))
            row = rows[0]
            self.assertEqual(('GET', 'download', 'HTTP/2', 200),
                (row['method'], row['stage'], row['http_version'], row['status']))
            self.assertFalse(row['body_complete'])
            self.assertIsNone(row['body_bytes'])
            self.assertEqual(0, row['body_bytes_observed'])
            self.assertIn([base64.b64encode(b'x-source').decode(), base64.b64encode(b'actual-get').decode()], row['headers_base64'])

    async def test_mid_download_error_preserves_bounded_observed_prefix_not_full_body(self):
        class InterruptedBody(httpx.AsyncByteStream):
            async def __aiter__(self):
                for _ in range(3):
                    yield b'x' * 32768
                raise httpx.ReadError('deliberate fixture disconnect')

        with tempfile.TemporaryDirectory() as temporary:
            _, rows = await self.stream_fixture(Path(temporary), lambda request:
                httpx.Response(200, stream=InterruptedBody(), headers={'x-source': 'actual-get', 'content-length': '131072'}))
            self.assertEqual(1, len(rows))
            row = rows[0]
            self.assertEqual(('GET', 'download', 200), (row['method'], row['stage'], row['status']))
            self.assertFalse(row['body_complete'])
            self.assertIsNone(row['body_bytes'])
            self.assertEqual(98304, row['body_bytes_observed'])
            self.assertTrue(row['body_truncated'])
            self.assertEqual(b'x' * 65536, base64.b64decode(row['body_base64']))
            self.assertEqual(hashlib.sha256(b'x' * 98304).hexdigest(), row['body_sha256'])
            self.assertEqual('decoded bytes yielded before interruption', row['body_sha256_scope'])

    async def test_download_failure_before_headers_does_not_capture_the_prior_put(self):
        def handler(request):
            raise httpx.ConnectError('deliberate fixture connection failure')
        with tempfile.TemporaryDirectory() as temporary:
            target, rows = await self.stream_fixture(Path(temporary), handler)
            self.assertEqual([], rows)
            self.assertIsNone(target.last_response.get())
            self.assertTrue((Path(temporary) / 'adapter-errors.jsonl').is_file())

    async def test_replay_500_never_becomes_ok_and_retains_exact_response(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            target = target_fixture(directory)
            original = httpx.Response(201, content=b'{"durable":"response"}')
            target.create = AsyncMock(return_value=(original, {
                'operationId': 'fixture-operation', 'title': 'input', 'responseVersion': 1, 'version': 1}))
            failure = b'{"code":"unexpected_provider_error","detail":"retained fixture"}'

            def handler(request):
                return (httpx.Response(500, content=failure, headers=[('x-fixture', 'one'), ('x-fixture', 'two')])
                        if request.method == 'POST' else httpx.Response(200, content=original.content))

            async with httpx.AsyncClient(base_url='http://fixture', transport=httpx.MockTransport(handler)) as client:
                target.client = client
                with self.assertRaisesRegex(IntegrityFailure, 'Normalized HTTP/domain result differs'):
                    await target.operation(9, {'kind': 'cycle', 'title': 'input'})
            rows = [json.loads(line) for line in (directory / 'http-failures.jsonl').read_text().splitlines()]
            replay = next(row for row in rows if row['stage'] == 'replay')
            self.assertEqual((500, 9, 'POST', '/v1/items'),
                             (replay['status'], replay['logical_id'], replay['method'], replay['path']))
            self.assertEqual(failure, base64.b64decode(replay['body_base64']))
            self.assertEqual(hashlib.sha256(failure).hexdigest(), replay['body_sha256'])
            self.assertEqual([[b'x-fixture', b'one'], [b'x-fixture', b'two']],
                [[base64.b64decode(name), base64.b64decode(value)] for name, value in replay['headers_base64']
                 if base64.b64decode(name) == b'x-fixture'])

    async def test_malformed_success_dto_is_retained_before_mutation_admission(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = target_fixture(Path(temporary))
            payload = b'{"title":"missing required operation identity"}'
            target.request = AsyncMock(return_value=httpx.Response(201, content=payload))
            with self.assertRaises(KeyError):
                await target.create(9, 'input')
            self.assertEqual([], target.mutations)
            row = json.loads((target.directory / 'http-failures.jsonl').read_text())
            self.assertEqual(201, row['status'])
            self.assertEqual('create', row['stage'])
            self.assertEqual(payload, base64.b64decode(row['body_base64']))

    async def test_equal_cycle_still_succeeds_and_records_no_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = target_fixture(Path(temporary))
            response = httpx.Response(201, content=b'valid durable response')
            target.create = AsyncMock(return_value=(response, {
                'operationId': 'fixture-operation', 'title': 'input', 'responseVersion': 1, 'version': 1}))
            target.request = AsyncMock(side_effect=[response, httpx.Response(200, content=response.content)])
            operation = {'kind': 'cycle', 'title': 'input'}
            self.assertEqual(('ok', expected_value(operation)), await target.operation(9, operation))
            self.assertFalse((target.directory / 'http-failures.jsonl').exists())

    async def test_failure_evidence_has_finite_record_header_and_body_bounds(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = target_fixture(Path(temporary))
            body = bytes(range(256)) * 300
            for _ in range(35):
                response = httpx.Response(500, content=body, headers={'x-long': 'a' * 20000})
                target.capture_failure(response, stage='fixture', method='GET', path='/fixture')
            rows = [json.loads(line) for line in (target.directory / 'http-failures.jsonl').read_text().splitlines()]
            self.assertEqual((32, 3), (len(rows), target.failure_capture_omitted))
            for row in rows:
                self.assertEqual(body[:65536], base64.b64decode(row['body_base64']))
                self.assertEqual(hashlib.sha256(body).hexdigest(), row['body_sha256'])
                self.assertEqual(len(body), row['body_bytes'])
                self.assertTrue(row['body_truncated'] and row['headers_truncated'])
                self.assertLessEqual(sum(len(base64.b64decode(value)) for pair in row['headers_base64'] for value in pair), 16384)


class IncompleteCohorts(unittest.TestCase):
    def test_two_valid_repetitions_cannot_replace_three_declared_repetitions(self):
        value = controller.Controller.__new__(controller.Controller)
        value.a21 = load_a21()
        case = {'id': 'fixture', 'provider': 'sqlite', 'protocol': 'http1', 'requests': 1}
        key = controller.case_id(case)
        value.cases, value.profile = [case], {'repetitions': 3, 'seed': 42}
        value.bands = {key: {'absolute_noise_ns': 1, 'relative_noise': 0}}
        value.calibration = {'fingerprint': 'fixture'}
        value.rows = [{'case': key, 'route': route, 'repetition': repeat, 'valid': True, 'successful': 1,
                       'records': [{'scheduled_ns': 0, 'completion_ns': 100 + repeat}]}
                      for route in value.a21.ROUTES for repeat in range(2)]
        self.assertEqual('inconclusive', value.comparisons()[key]['decision'])
        for route in value.a21.ROUTES:
            value.rows.append({'case': key, 'route': route, 'repetition': 2, 'valid': True, 'successful': 1,
                               'records': [{'scheduled_ns': 0, 'completion_ns': 102}]})
        self.assertEqual({'managed-aot', 'nativeweave-aot-rust'}, set(value.comparisons()[key]))


class MalformedWorkerResult(unittest.IsolatedAsyncioTestCase):
    async def test_missing_valid_is_retained_as_invalid_before_stopping_the_cohort(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            value = controller.Controller.__new__(controller.Controller)
            value.a21 = load_a21()
            value.args = SimpleNamespace(phase='functional', a21=ROOT / 'src/Benchmarks.Harness/nativeweave_bench.py')
            value.output, value.rows = directory, []
            value.profile = {'seed': 42}
            value.cases = [{'id': name, 'provider': 'sqlite', 'protocol': 'http1', 'kind': 'create', 'requests': 1}
                           for name in ('first', 'second')]
            value.summary = {'runs': [], 'unsupported': []}
            plan = {'routes': [{'id': 'direct-rust'}], 'supervision': {'worker_timeout_seconds': 1, 'termination_grace_seconds': .1}}
            descriptors = {'direct-rust': {'id': 'direct-rust', 'supported': {
                'providers': ['sqlite'], 'protocols': ['http1'], 'kinds': ['create']}}}
            value.plan = lambda case, settings: ((plan, descriptors, directory), None)
            application = directory / 'application'
            save(application / 'receipt.json', {'status': 'passed'})

            async def worker(command, supervision, *args):
                output = Path(command[command.index('--output') + 1])
                save(output, {'hello': {'receipt_directory': str(application)}, 'route': 'direct-rust', 'repetition': 0})
                receipt = {'status': 'completed', 'failure': None, 'owned_processes': [], 'live_owned_processes_after_cleanup': []}
                save(supervision / 'receipt.json', receipt)
                save(directory / 'control.json', {'stop': True})
                return receipt

            with patch.object(controller, 'supervised_command', worker):
                self.assertEqual(1, await value.run({}))
            summary = load(directory / 'summary.json')
            self.assertEqual('stopped-incomplete', summary['state'])
            self.assertEqual(1, len(summary['not_run']))
            self.assertFalse(summary['runs'][0]['valid'])
            row = load(directory / summary['runs'][0]['path'])
            self.assertIn('valid must be Boolean', row['invalid_reasons'][0])
            self.assertTrue(Path(row['original_a21_observation']['path']).exists())

    def test_valid_shape_checks_downstream_statistical_fields(self):
        a21 = load_a21()
        valid = {'route': 'direct-rust', 'repetition': 0, 'valid': True, 'invalid_reasons': [],
            'hello': {'receipt_directory': '/fixture'},
            'records': [{'id': 0, 'scheduled_ns': 1, 'completion_ns': 2, 'dispatch_ns': 1, 'admission_ns': 1, 'status': 'ok'}],
            'admitted': 1, 'completed': 1, 'successful': 1, 'outcome_counts': {'ok': 1},
            'histograms': {'arrival_all_outcomes': {'resolution_ns': 1, 'maximum_ns': 100}},
            'measurement_window_ns': {'elapsed': 1}, 'rates_per_second': {'offered': 1}}
        controller.validate_observation(valid, 'direct-rust', 0, 1, a21)
        for field in ('records', 'histograms', 'measurement_window_ns', 'rates_per_second', 'successful'):
            with self.subTest(field=field):
                malformed = dict(valid)
                del malformed[field]
                with self.assertRaisesRegex(ValueError, 'Malformed A21 observation'):
                    controller.validate_observation(malformed, 'direct-rust', 0, 1, a21)


@unittest.skipUnless(sys.platform == 'linux' and hasattr(os, 'pidfd_open') and hasattr(signal, 'pidfd_send_signal'),
                     'Real supervisor fixtures require the selected Linux pidfd profile')
class ProcessSupervision(unittest.IsolatedAsyncioTestCase):
    async def run_fixture(self, directory, source, timeout=1, grace=.1):
        fixture = directory / 'fixture.py'
        fixture.write_text('import sys\nsys.path.insert(0, ' + repr(str(SOURCE)) + ')\n' + source)
        return await supervised_command([sys.executable, str(fixture)], directory / 'supervision',
            timeout, grace, directory / 'control.json')

    async def test_normal_worker_completes_without_forced_cleanup(self):
        with tempfile.TemporaryDirectory() as temporary:
            receipt = await self.run_fixture(Path(temporary), 'import time\ntime.sleep(.15)\n')
            self.assertEqual('completed', receipt['status'])
            self.assertEqual([], receipt['signals'])
            self.assertEqual([], receipt['live_owned_processes_after_cleanup'])

    async def test_watchdog_kills_registered_detached_children_but_not_unrelated_process(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            unrelated = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])
            try:
                receipt = await self.run_fixture(directory,
                    'import os, signal, subprocess, time\n'
                    'from supervision import register_process\n'
                    'child = subprocess.Popen([sys.executable, "-c", '
                    '"import signal,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);time.sleep(30)"], start_new_session=True)\n'
                    'register_process("application", child.pid, "fixture-app")\n'
                    'signal.signal(signal.SIGTERM, signal.SIG_IGN)\n'
                    'time.sleep(30)\n', timeout=.4)
                self.assertEqual('failed', receipt['status'])
                self.assertEqual('external cell watchdog deadline', receipt['failure'])
                self.assertLess(receipt['elapsed_ns'], 4_000_000_000)
                self.assertEqual([], receipt['live_owned_processes_after_cleanup'])
                self.assertTrue(any(row['role'] == 'application' for row in receipt['owned_processes']))
                self.assertTrue(any(row['signal'] == signal.SIGKILL for row in receipt['signals']))
                self.assertNotIn(unrelated.pid, {row['pid'] for row in receipt['signals']})
                self.assertIsNone(unrelated.poll())
                self.assertIn('never proves native retirement', receipt['application_retirement'])
            finally:
                unrelated.terminate(); unrelated.wait(timeout=5)

    async def test_exited_worker_with_registered_orphan_is_not_a_success(self):
        with tempfile.TemporaryDirectory() as temporary:
            receipt = await self.run_fixture(Path(temporary),
                'import subprocess,time\nfrom supervision import register_process\n'
                'child = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(30)"], start_new_session=True)\n'
                'register_process("application", child.pid, "fixture-app")\n'
                'time.sleep(.15)\n')
            self.assertEqual('worker exited with a live owned descendant', receipt['failure'])
            self.assertEqual('failed', receipt['status'])
            self.assertEqual([], receipt['live_owned_processes_after_cleanup'])

    async def test_operator_stop_is_explicit_and_never_completed(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / 'control.json').write_text('{"stop":true}')
            receipt = await self.run_fixture(directory, 'import time\ntime.sleep(30)\n')
            self.assertEqual('operator stop control', receipt['failure'])
            self.assertEqual('failed', receipt['status'])
            self.assertEqual([], receipt['live_owned_processes_after_cleanup'])
            self.assertEqual(receipt, load(directory / 'supervision/receipt.json'))


if __name__ == '__main__':
    unittest.main()
