"""Finite observer lifecycle tests; no application, database or benchmark load."""
import asyncio
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import threading
import unittest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


a21 = module('observer_test_a21', ROOT / 'src/Benchmarks.Harness/nativeweave_bench.py')
resources = module('observer_test_resources', HERE / 'perf01_resources.py')


@unittest.skipUnless(sys.platform == 'linux', 'Linux-only measurement candidate')
class ObserverSamplingTests(unittest.TestCase):
    def run_case(self, scenario, native_threads):
        try:
            asyncio.run(scenario())
        finally:
            # This is actual thread teardown after the EXISTING asyncio.run
            # default-executor shutdown, not an inference from Future.done().
            for identifier in native_threads:
                self.assertFalse(Path('/proc/self/task', str(identifier)).exists())
                self.assertFalse(any(t.is_alive() and t.native_id == identifier for t in threading.enumerate()))

    def test_blocking_reads_leave_loop_live_and_one_job_per_serial_sampler(self):
        native_threads = set()
        async def scenario():
            loop = asyncio.get_running_loop()
            entered = [asyncio.Event() for _ in range(3)]
            published = [asyncio.Event() for _ in range(3)]
            releases = [threading.Event() for _ in range(3)]
            stop = asyncio.Event()
            counts = {'calls': 0, 'active': 0, 'maximum': 0}
            rows = []
            caller = threading.get_native_id()
            affinity = sorted(os.sched_getaffinity(0))
            def read():
                index = counts['calls']; counts['calls'] += 1
                counts['active'] += 1
                counts['maximum'] = max(counts['maximum'], counts['active'])
                native_threads.add(threading.get_native_id())
                loop.call_soon_threadsafe(entered[index].set)
                try:
                    if not releases[index].wait(3):
                        raise TimeoutError('Test release did not arrive')
                    return {'index': index, 'counter': 7}
                finally:
                    counts['active'] -= 1
            def publish(row):
                rows.append(row)
                published[row['index']].set()
            async def sampler():
                for _ in range(3):
                    await a21.sample_in_worker(stop, read, publish)
            task = asyncio.create_task(sampler())
            try:
                for index in range(3):
                    await asyncio.wait_for(entered[index].wait(), 3)
                    await asyncio.sleep(0)  # Can progress while the read is blocked.
                    self.assertEqual(counts['calls'], index + 1)
                    self.assertEqual(counts['active'], 1)
                    self.assertEqual(len(rows), index)
                    releases[index].set()
                    await asyncio.wait_for(published[index].wait(), 3)
                await task
                self.assertEqual(counts['maximum'], 1)
                self.assertEqual(counts['active'], 0)
                for row in rows:
                    worker = row['sampling_worker']
                    self.assertNotEqual(worker['worker_native_thread_id'], caller)
                    self.assertEqual(worker['caller_native_thread_id'], caller)
                    self.assertEqual(worker['worker_affinity_cpus'], affinity)
                    self.assertLessEqual(worker['submitted_ns'], worker['started_ns'])
                    self.assertLessEqual(worker['started_ns'], row['time_ns'])
                    self.assertLessEqual(row['finished_ns'], worker['finished_ns'])
            finally:
                for release in releases:
                    release.set()
                await asyncio.gather(task, return_exceptions=True)
        self.run_case(scenario, native_threads)

    def test_repeat_cancellation_joins_and_publishes_once_before_close(self):
        native_threads = set()
        async def scenario():
            loop = asyncio.get_running_loop()
            entered, stop = asyncio.Event(), asyncio.Event()
            release = threading.Event()
            phase = ['measurement']
            output = io.StringIO()
            calls = []
            def read():
                native_threads.add(threading.get_native_id()); calls.append('read')
                loop.call_soon_threadsafe(entered.set)
                if not release.wait(3):
                    raise TimeoutError('Test release did not arrive')
                calls.append('finished-read')
                return {'counter': 9}
            def publish(row):
                self.assertFalse(output.closed)
                self.assertEqual(calls[-1], 'finished-read')
                calls.append('publish')
                output.write(json.dumps(row) + '\n')
            task = asyncio.create_task(a21.sample_in_worker(stop, read, publish, lambda: phase[0]))
            try:
                await asyncio.wait_for(entered.wait(), 3)
                task.cancel('first cancellation')
                await asyncio.sleep(0)
                task.cancel('second cancellation')
                await asyncio.sleep(0)
                self.assertFalse(task.done())
                self.assertEqual(output.getvalue(), '')
                phase[0] = 'post-load-idle'
                stop.set(); release.set()
                with self.assertRaises(asyncio.CancelledError) as caught:
                    await task
                self.assertEqual(str(caught.exception), 'first cancellation')
                rows = [json.loads(line) for line in output.getvalue().splitlines()]
                self.assertEqual(len(rows), 1)
                self.assertEqual(rows[0]['phase'], 'measurement')
                self.assertEqual(rows[0]['phase_at_finish'], 'post-load-idle')
                self.assertEqual(rows[0]['sampling_worker']['submission_phase'], 'measurement')
                self.assertEqual(calls, ['read', 'finished-read', 'publish'])
                self.assertFalse(await a21.sample_in_worker(stop, read, publish))
                self.assertEqual(calls, ['read', 'finished-read', 'publish'])
            finally:
                release.set()
                await asyncio.gather(task, return_exceptions=True)
                output.close()
        self.run_case(scenario, native_threads)

    def test_worker_cancel_error_and_publish_error_are_failures_not_loops(self):
        native_threads = set()
        async def scenario():
            stop = asyncio.Event()
            published = []
            for error in (asyncio.CancelledError('worker'), RuntimeError('read failure')):
                def read(error=error):
                    native_threads.add(threading.get_native_id())
                    raise error
                with self.assertRaises(type(error)):
                    await asyncio.wait_for(a21.sample_in_worker(stop, read, published.append), 3)
            self.assertEqual(published, [])
            def read_ok():
                native_threads.add(threading.get_native_id())
                return {'counter': 1}
            def broken_publish(row):
                raise OSError('publication failed')
            with self.assertRaisesRegex(OSError, 'publication failed'):
                await a21.sample_in_worker(stop, read_ok, broken_publish)
        self.run_case(scenario, native_threads)

    def test_worker_phase_is_actual_start_not_submission_or_return_label(self):
        native_threads = set()
        async def scenario():
            from concurrent.futures import ThreadPoolExecutor
            loop = asyncio.get_running_loop()
            # Queue the sample behind one finite test job so the phase can
            # change after submission but before its actual worker start.
            loop.set_default_executor(ThreadPoolExecutor(max_workers=1))
            entered = asyncio.Event(); release = threading.Event()
            def blocker():
                native_threads.add(threading.get_native_id())
                loop.call_soon_threadsafe(entered.set)
                if not release.wait(3): raise TimeoutError('Test release did not arrive')
            blocker_job = loop.run_in_executor(None, blocker)
            phase = ['startup-and-seed']; rows = []; stop = asyncio.Event()
            task = asyncio.create_task(a21.sample_in_worker(stop, lambda: {'counter': 2}, rows.append, lambda: phase[0]))
            try:
                await asyncio.wait_for(entered.wait(), 3)
                await asyncio.sleep(0)
                phase[0] = 'measurement'; release.set()
                await blocker_job; await task
                phase[0] = 'post-load-idle'
                worker = rows[0]['sampling_worker']
                self.assertEqual(worker['submission_phase'], 'startup-and-seed')
                self.assertEqual(worker['phase_started'], 'measurement')
                self.assertEqual(worker['phase_finished'], 'measurement')
                self.assertEqual(rows[0]['phase'], 'measurement')
            finally:
                release.set()
                await asyncio.gather(blocker_job, task, return_exceptions=True)
        self.run_case(scenario, native_threads)

    def test_overlap_rss_cannot_hide_entire_measurement_phase(self):
        def row(phase, start, end, memory):
            return {'phase': phase, 'phase_at_finish': 'post-load-idle', 'time_ns': start,
                    'finished_ns': end, 'pid': 1, 'start_ticks': 2, 'identity_matches': True,
                    'clock_ticks_per_second': 100, 'cpu_ticks': 3, 'cpu_observed_ns': start + 1,
                    'fd_count': 4, 'socket_count': 1, 'descriptor_scan_misses': 0,
                    'affinity_cpus': [8], 'counters': {'rss_bytes': {'value': memory}},
                    'sampling_worker': {'started_ns': start, 'finished_ns': end}}
        crossed = row('startup-and-seed', 10, 100, 512 * 1024**2)
        # Both phase labels are outside a complete measured window.
        legacy = resources.summarize([crossed])
        self.assertNotIn('measurement', legacy)
        observed = resources.summarize([crossed], {'start': 40, 'end': 60})['measurement']
        self.assertEqual(observed['rss_peak_bytes'], 512 * 1024**2)
        self.assertEqual(observed['cpu_samples'], 0)
        self.assertIsNone(observed['cpu_seconds'])
        self.assertGreater(observed['rss_peak_bytes'], 256 * 1024**2)
        # Preserve the original phase-based maximum even outside that window.
        measured = row('measurement', 120, 140, 768 * 1024**2)
        observed = resources.summarize([crossed, measured], {'start': 40, 'end': 60})['measurement']
        self.assertEqual(observed['rss_peak_bytes'], 768 * 1024**2)
        self.assertEqual(observed['rss_window_overlap_samples'], 1)

    def test_default_sync_and_unknown_policy(self):
        self.assertFalse(a21.threaded_observation(None))
        self.assertFalse(a21.threaded_observation({'mode': 'synchronous'}))
        self.assertTrue(a21.threaded_observation({'mode': 'threaded'}))
        with self.assertRaises(ValueError):
            a21.threaded_observation({'mode': 'unknown'})


if __name__ == '__main__':
    unittest.main(verbosity=2)
