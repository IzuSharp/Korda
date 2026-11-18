#!/usr/bin/env python3
"""B23 assembled domain measurements using the accepted A21 controller.

Run functional comparison first, independent calibration second, and a new
confirmation cohort against the frozen measured noise bands. No command builds
or changes a product/native artifact. Fault injection belongs to B22 and must
not share a timed execution window.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
from contextlib import contextmanager
import hashlib
import importlib.metadata
import importlib.util
import json
import os
from pathlib import Path
import random
import socket
import subprocess
import sys

from common import check_files, digest, expected_value, inventory, load, load_a21, payload_hash, save, source_identity

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent


def comparator_route(name, route, manifest):
    """Resolve repository-relative source/lock pins without changing their bytes."""
    route = dict(route, id=name)
    relative = [path for path in route['lock_hashes'] if not Path(path).is_absolute()]
    ancestors = [manifest.resolve().parent, *manifest.resolve().parents]
    base = next((parent for parent in ancestors if all((parent / path).is_file() for path in relative)), None)
    if base is None:
        raise ValueError('Cannot resolve comparator lockfiles relative to its manifest repository')
    for group in ('artifacts', 'lock_hashes'):
        route[group] = {str(Path(path) if Path(path).is_absolute() else base / path): value
                        for path, value in route[group].items()}
        check_files(route[group])
    route['source'] = route.get('source', route.get('source_identity', {}))
    if 'files' in route['source']:
        check_files({str(base / path): value for path, value in route['source']['files'].items()})
        route['source'] = dict(route['source'], repository=str(base))
    if not Path(route['command'][0]).is_absolute():
        route['command'] = [str(base / route['command'][0]), *route['command'][1:]]
    return route


def product_route(probe_directory):
    root = Path(probe_directory).resolve()
    receipt = load(root / 'progress.json')
    build = receipt['builds']['baseline-aot']
    if build['status'] != 'passed':
        raise ValueError('The reference application AOT build is not complete')
    artifacts = {str(root / build['artifact_directory'] / path): value for path, value in build['artifacts_sha256'].items()}
    locks = {str(root / build['metadata_directory'] / path): value for path, value in build['metadata_sha256'].items()}
    check_files(artifacts); check_files(locks)
    return {'id': 'nativeweave-aot-rust', 'command': [str(root / build['executable'])],
        'artifacts': artifacts, 'lock_hashes': locks,
        'source': receipt['source_repository'], 'toolchain': receipt.get('environment', {}),
        'package_probe_receipt': {'path': str(root / 'progress.json'), 'sha256': digest(root / 'progress.json')},
        'native': build['native'], 'supported': {'providers': ['sqlite', 'postgresql'],
            'protocols': ['http1', 'http1-tls', 'http2-tls'], 'kinds': ['create', 'cycle', 'list', 'stream']},
        'limitations': ['No public per-operation FFI/copy/native-allocation counter is present in the accepted application'],
        'instrumented': False}


def select_cases(profile, mode):
    cases = []
    for workload in profile['workloads']:
        cases.append(dict(workload, provider='sqlite', protocol='http1'))
    if mode in ('extended', 'all'):
        by_id = {value['id']: value for value in profile['workloads']}
        for name in ('warm-cycle-high', 'warm-keyset-page', 'stream-one-mib'):
            cases.append(dict(by_id[name], provider='postgresql', protocol='http1'))
        for provider in ('sqlite', 'postgresql'):
            for protocol in ('http1-tls', 'http2-tls'):
                for name in ('warm-cycle-high', 'stream-one-mib'):
                    cases.append(dict(by_id[name], provider=provider, protocol=protocol))
    if mode in ('diagnostics', 'all'):
        for name in profile['diagnostics_profiles']:
            cases.append({'id': 'diagnostics-' + name, 'kind': 'diagnostic-failure',
                'requests': 2300 if name == 'exporter-saturation' else 300,
                'rate': 100, 'warmup': 16, 'client_concurrency': 16,
                'cache': 'Warm typed query/mapping path followed by an intentional diagnostic failure',
                'provider': 'sqlite', 'protocol': 'http1', 'diagnostics': name, 'product_only': True})
        for advanced in (False, True):
            cases.append({'id': 'advanced-di-list' if advanced else 'minimal-list', 'kind': 'list', 'requests': 1200, 'rate': 200,
                'warmup': 32, 'client_concurrency': 16, 'limit': 100, 'provider': 'sqlite', 'protocol': 'http1',
                'cache': 'Same 128 rows, typed query and observation binary; request DI versus captured service',
                'diagnostics': 'release-disabled', 'di': advanced, 'product_only': True})
    if mode == 'diagnostics':
        cases = [case for case in cases if 'diagnostics' in case]
    return cases


def case_id(case):
    return case['id'] + '-' + case['provider'] + '-' + case['protocol']


def operation(case, seed, index):
    value = {'kind': case['kind']}
    if case['kind'] in ('create', 'cycle'):
        prefix = 'task-' + str(seed) + '-' + str(index) + '-'
        value['title'] = (prefix + 'x' * case['title_bytes'])[:case['title_bytes']]
    elif case['kind'] == 'list':
        value['limit'] = case['limit']
    elif case['kind'] == 'stream':
        value.update(bytes=case['bytes'], seed=seed, sha256=payload_hash(seed, case['bytes']))
    return value


def dataset(case, seed):
    values = []
    for index in range(case['requests']):
        item = operation(case, seed, index)
        token = base64.urlsafe_b64encode(json.dumps(item, separators=(',', ':')).encode()).decode()
        values.append({'input': token, 'expected': expected_value(item)})
    return values


def unsupported(route, case):
    if case.get('product_only') and route['id'] != 'nativeweave-aot-rust':
        return 'No equivalent implementation of this product resource/diagnostic profile'
    support = route['supported']
    for plural, selected in [('providers', case['provider']), ('protocols', case['protocol']), ('kinds', case['kind'])]:
        if selected not in support.get(plural, []):
            return 'Comparator does not support the frozen ' + plural + ' value: ' + selected
    return None


def calibration_bands(rows, resolution_ns, repetitions=3):
    """Freeze observed within-route process variability; no invented percentage."""
    a21 = load_a21()
    by_case, declared, failed = {}, {}, set()
    for row in rows:
        declared.setdefault(row['case'], set()).add(row['route'])
        if not row.get('valid') or not row.get('records'):
            failed.add(row['case'])
            continue
        values = [record['completion_ns'] - record['scheduled_ns'] for record in row['records']]
        mean = a21.statistics.mean(values)
        by_case.setdefault(row['case'], {}).setdefault(row['route'], []).append(mean)
    result = {}
    for key, routes in by_case.items():
        eligible = {route: means for route, means in routes.items() if len(means) == repetitions}
        if key in failed or set(eligible) != declared[key]:
            continue
        noise = max([resolution_ns] + [max(means) - min(means) for means in eligible.values()])
        result[key] = {'absolute_noise_ns': noise, 'relative_noise': 0,
            'process_means_ns': eligible,
            'derivation': 'Maximum measured within-route range of independent calibration process means, at least one histogram bucket'}
    return result


@contextmanager
def postgres(directory, binaries):
    directory.mkdir(parents=True, exist_ok=False)
    binaries = Path(binaries).resolve()
    commands = []

    def command(arguments):
        result = subprocess.run([str(value) for value in arguments], capture_output=True, text=True, timeout=120)
        log = directory / ('command-' + str(len(commands)) + '.log')
        log.write_text(result.stdout + result.stderr)
        commands.append({'argv': [str(value) for value in arguments], 'exit_code': result.returncode,
                         'log': log.name, 'log_sha256': digest(log)})
        save(directory / 'commands.json', commands)
        if result.returncode:
            raise RuntimeError('PostgreSQL command failed; inspect ' + str(log))
        return result.stdout.strip()

    version = command([binaries / 'postgres', '--version'])
    if not version.endswith(' 18.0'):
        raise ValueError('B23 requires the frozen PostgreSQL 18.0 profile')
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0)); port = listener.getsockname()[1]
    data = directory / 'data'
    command([binaries / 'initdb', '-D', data, '-U', 'nativeweave', '--auth=trust', '--no-locale', '--encoding=UTF8'])
    command([binaries / 'pg_ctl', '-D', data, '-l', directory / 'server.log', '-o',
        f"-h 127.0.0.1 -p {port} -k '' -c fsync=on -c synchronous_commit=on -c full_page_writes=on", '-w', 'start'])
    uri = f'postgresql://nativeweave@127.0.0.1:{port}/postgres'
    settings = command([binaries / 'psql', uri, '-X', '-At', '-c',
        "SELECT current_setting('server_version'),current_setting('fsync'),current_setting('synchronous_commit'),current_setting('full_page_writes')"])
    save(directory / 'identity.json', {'version': version, 'observed_settings': settings,
        'postgres_sha256': digest(binaries / 'postgres'), 'psql_sha256': digest(binaries / 'psql'),
        'scope': 'Isolated local cluster, target apps and clients are descendants of the same execution'})
    try:
        yield {'postgres_base': uri, 'psql': str(binaries / 'psql')}
    finally:
        command([binaries / 'pg_ctl', '-D', data, '-m', 'fast', '-w', 'stop'])


class Controller:
    def __init__(self, args):
        self.args = args
        self.a21 = load_a21(args.a21)
        self.profile = load(args.profile)
        self.output = args.output.resolve()
        self.output.mkdir(parents=True, exist_ok=False)
        self.routes = {'nativeweave-aot-rust': product_route(args.package_probe)}
        if args.comparators:
            comparators = load(args.comparators)
            for name, route in comparators['routes'].items():
                self.routes[name] = comparator_route(name, route, args.comparators)
        if args.phase != 'functional' and set(self.routes) != self.a21.ROUTES:
            raise ValueError('Timed comparison requires manifests for all three routes, including explicitly unsupported cells')
        self.instrumented = load(args.instrumented)['profiles'] if args.instrumented else {}
        self.cases = select_cases(self.profile, args.cases)
        if args.select:
            selectors = args.select.split(',')
            self.cases = [case for case in self.cases if any(value in (case['id'], case_id(case)) for value in selectors)]
        if not self.cases:
            raise ValueError('No selected workload cells')
        if args.phase == 'functional':
            self.cases = [dict(case, requests=2, warmup=1, rate=10,
                               client_concurrency=min(4, case['client_concurrency'])) for case in self.cases]
        self.files = {str(path): digest(path) for path in [HERE / 'run.py', HERE / 'http_target.py', HERE / 'common.py',
            args.a21.resolve(), args.golden.resolve(), args.profile.resolve()]}
        self.external_inputs = {}
        if any(case['protocol'] != 'http1' for case in self.cases):
            for name in ('localhost.pem', 'localhost.private.pem'):
                path = args.package_probe.resolve() / 'tls' / name
                self.external_inputs[str(path)] = digest(path)
        if any(case['provider'] == 'postgresql' for case in self.cases):
            if not args.postgres_bin:
                raise ValueError('PostgreSQL cells require pinned PostgreSQL 18.0 binaries')
            for name in ('postgres', 'psql', 'initdb', 'pg_ctl'):
                path = args.postgres_bin.resolve() / name
                self.external_inputs[str(path)] = digest(path)
        self.files.update(self.external_inputs)
        self.client_libraries = {}
        for name in ('httpx', 'httpcore', 'h11', 'h2', 'hpack', 'hyperframe'):
            path = Path(importlib.util.find_spec(name).origin).resolve()
            self.files[str(path)] = digest(path)
            self.client_libraries[name] = {'version': importlib.metadata.version(name),
                'module': str(path), 'module_sha256': digest(path)}
        self.fingerprint = hashlib.sha256(json.dumps({'profile': digest(args.profile), 'cases': self.cases,
            'routes': self.routes, 'instrumented': self.instrumented, 'controller_files': self.files}, sort_keys=True).encode()).hexdigest()
        self.calibration = load(args.calibration / 'summary.json') if args.calibration else None
        if args.phase == 'measure':
            if not self.calibration or self.calibration.get('phase') != 'calibrate' or self.calibration['fingerprint'] != self.fingerprint:
                raise ValueError('Confirmation requires matching immutable calibration inputs')
            self.bands = self.calibration['noise_bands']
            required_cases = {case_id(case) for case in self.cases}
            if not self.calibration.get('all_executed_cells_valid') or not required_cases.issubset(self.bands):
                raise ValueError('Confirmation requires complete valid calibration repetitions and a measured band for every selected case')
            save(self.output / 'frozen-noise-bands.json', {'calibration_sha256': digest(args.calibration / 'summary.json'),
                                                       'bands': self.bands})
        else:
            self.bands = {}
        self.summary = {'schema_version': 1, 'task': 'B23', 'platform': 'linux-x64', 'phase': args.phase,
            'fingerprint': self.fingerprint, 'profile_sha256': digest(args.profile),
            'a21_sha256': digest(args.a21), 'controller_source': source_identity(ROOT),
            'host': self.a21.host_metadata(), 'isolation_note': args.isolation_note,
            'client_libraries': self.client_libraries,
            'external_input_sha256': self.external_inputs,
            'cases': self.cases, 'routes': self.routes, 'runs': [], 'unsupported': [],
            'state': 'running', 'acceptance_gate': 'B22 correctness/fault/soak acceptance remains separately required',
            'claim': 'Functional comparison only; timing outputs do not constitute performance evidence' if args.phase == 'functional'
                     else 'Only exact declared workload/route cells; unsupported, failed and generator-saturated observations remain explicit'}
        save(self.output / 'summary.json', self.summary)
        self.rows = []

    def plan(self, case, postgres_settings):
        identifier = case_id(case)
        directory = self.output / 'plans' / identifier
        directory.mkdir(parents=True, exist_ok=False)
        dataset_path = directory / 'dataset.json'
        save(dataset_path, dataset(case, self.profile['seed']))
        selected_routes = dict(self.routes)
        if case.get('diagnostics'):
            mode = case['diagnostics']
            if mode not in self.instrumented:
                return None, 'No built instrumentation artifact for ' + mode
            selected_routes['nativeweave-aot-rust'] = dict(self.instrumented[mode], id='nativeweave-aot-rust')
        manifest = {'schema_version': 1, 'profile': case, 'provider': case['provider'],
            'protocol': case['protocol'], 'routes': selected_routes,
            'requests': case['requests'], 'max_in_flight': self.profile['settings']['max_in_flight'],
            'diagnostic_snapshot_settle_ms': self.profile['settings']['diagnostic_snapshot_settle_ms'],
            'run_directory': str(self.output / 'applications'), 'files': self.files,
            'golden': str(self.args.golden.resolve()), 'di': case.get('di', False), **postgres_settings}
        if case['protocol'] != 'http1':
            manifest.update(tls_certificate=str(self.args.package_probe.resolve() / 'tls/localhost.pem'),
                            tls_private_key=str(self.args.package_probe.resolve() / 'tls/localhost.private.pem'))
        manifest_path = directory / 'target.json'
        save(manifest_path, manifest)
        native_route = self.routes['nativeweave-aot-rust']
        settings = self.profile['settings']
        routes = []
        for name, route in selected_routes.items():
            artifacts = {**route['artifacts'], **self.files, str(manifest_path): digest(manifest_path),
                         str(Path(sys.executable).resolve()): digest(Path(sys.executable).resolve())}
            command = [str(Path(sys.executable).resolve()), str(HERE / 'http_target.py'),
                       '--manifest', str(manifest_path), '--route', name, '--a21', str(self.args.a21.resolve())]
            routes.append({'id': name, 'command': command, 'artifacts': artifacts,
                           'lock_hashes': route['lock_hashes'], 'toolchain': route['toolchain']})
        band = self.bands.get(identifier)
        regression = {'enabled': bool(band) and self.args.phase == 'measure',
            'baseline_id': self.calibration['fingerprint'] if self.calibration else 'calibration-only-no-comparison',
            'absolute_noise_ns': band['absolute_noise_ns'] if band else 0, 'relative_noise': 0}
        plan = {'schema_version': 1, 'workload_id': identifier, 'workload_kind': 'domain',
            'source_commit': native_route['source'].get('commit'), 'seed': self.profile['seed'],
            'repetitions': self.profile['calibration_repetitions'] if self.args.phase == 'calibrate' else self.profile['repetitions'],
            'oracle': {'kind': 'expected-values', 'dataset': str(dataset_path), 'sha256': digest(dataset_path)},
            'settings': {'cache': case['cache'], 'connection_reuse': 'Persistent HTTP client, declared bounded pool',
                'durability': settings[case['provider']], 'tls': case['protocol'],
                'serialization': 'D20 static v1 JSON and length-prefixed UTF-8 request hash; independent DTO/durable oracle',
                'allocator': 'Actual pinned route allocator; private RSS and unavailable allocator internals reported separately',
                'isa_profile': settings['isa_profile'], 'warmup_requests': case['warmup'],
                'features': {'provider': case['provider'], 'protocol': case['protocol'], 'workload': case['kind']},
                'instrumentation': case.get('diagnostics', 'External client/procfs observer; no product listener')},
            'timing': {'requests': case['requests'], 'period_ns': round(1e9 / case['rate']),
                'timeout_ns': settings['measurement_timeout_seconds'] * 1_000_000_000,
                'max_dispatch_lag_ns': settings['max_dispatch_lag_ms'] * 1_000_000,
                'max_in_flight': settings['max_in_flight'], 'cleanup_timeout_ns': settings['cleanup_timeout_seconds'] * 1_000_000_000},
            'histogram': {'resolution_ns': settings['histogram_resolution_ns'], 'maximum_ns': settings['histogram_maximum_ns']},
            'regression': regression, 'routes': routes,
            'engineering_cost': {'source': 'Accepted B21 packed consumer plus separately built comparator executables',
                'application_rebuild_scope': 'Managed source only; immutable native module hashes',
                'counter_limitations': 'No inferred per-operation FFI/copy/native allocation counts'}}
        if set(selected_routes) == self.a21.ROUTES:
            self.a21.validate_plan(plan, directory)
        elif self.args.phase != 'functional':
            raise ValueError('Missing required comparison route')
        save(directory / 'plan.json', plan)
        return (plan, selected_routes, directory), None

    async def run(self, postgres_settings):
        jobs = []
        for case in self.cases:
            prepared, error = self.plan(case, postgres_settings)
            if error:
                self.summary['unsupported'].append({'case': case_id(case), 'route': 'nativeweave-aot-rust', 'reason': error})
                continue
            plan, descriptors, directory = prepared
            repetitions = 1 if self.args.phase == 'functional' else plan['repetitions']
            for route in plan['routes']:
                reason = unsupported(descriptors[route['id']], case)
                if reason:
                    self.summary['unsupported'].append({'case': case_id(case), 'route': route['id'], 'reason': reason})
                    continue
                for repetition in range(repetitions):
                    jobs.append((case, plan, route, directory, repetition))
        random.Random(self.profile['seed'] + (1 if self.args.phase == 'measure' else 0)).shuffle(jobs)
        self.summary['run_order'] = [[case_id(case), route['id'], rep] for case, _, route, _, rep in jobs]
        save(self.output / 'summary.json', self.summary)
        for case, plan, route, directory, repetition in jobs:
            name = case_id(case) + '--' + route['id'] + '--' + str(repetition)
            destination = self.output / 'observations' / (name + '.json')
            destination.parent.mkdir(exist_ok=True)
            print('RUN ' + name, flush=True)
            try:
                row = await self.a21.run_cell(plan, route, repetition, directory, destination)
                row['case'] = case_id(case)
                app_path = Path(row['hello']['receipt_directory']) / 'receipt.json'
                app = load(app_path)
                row['application_receipt'] = {'path': str(app_path), 'sha256': digest(app_path)}
                if app['status'] != 'passed':
                    row['valid'] = False
                    row['invalid_reasons'].append('Application durable oracle or bounded shutdown failed')
            except Exception as error:
                row = {'case': case_id(case), 'route': route['id'], 'repetition': repetition,
                       'valid': False, 'invalid_reasons': [type(error).__name__ + ': ' + str(error)],
                       'stage': 'startup/protocol', 'claim': 'Failed cell retained, never a performance success'}
            save(destination, row)
            self.rows.append(row)
            self.summary['runs'].append({'case': row['case'], 'route': row['route'], 'repetition': repetition,
                'path': str(destination.relative_to(self.output)), 'sha256': digest(destination), 'valid': row['valid'],
                'invalid_reasons': row.get('invalid_reasons', []), 'outcome_counts': row.get('outcome_counts', {})})
            save(self.output / 'summary.json', self.summary)
        if self.args.phase == 'calibrate':
            self.summary['noise_bands'] = calibration_bands(self.rows, self.profile['settings']['histogram_resolution_ns'],
                                                            self.profile['calibration_repetitions'])
        elif self.args.phase == 'measure':
            self.summary['noise_bands'] = self.bands
            self.summary['comparisons'] = self.comparisons()
        self.summary['all_executed_cells_valid'] = bool(self.rows) and all(row['valid'] for row in self.rows)
        self.summary['state'] = 'completed' if self.summary['all_executed_cells_valid'] else 'completed-with-invalid-cells'
        save(self.output / 'summary.json', self.summary)
        return 0 if self.summary['all_executed_cells_valid'] else 1

    def comparisons(self):
        result = {}
        for case in self.cases:
            name = case_id(case)
            matching = [row for row in self.rows if row['case'] == name]
            means = {}
            for route in self.a21.ROUTES:
                cells = [row for row in matching if row['route'] == route]
                if cells and all(row['valid'] and row.get('successful') == case['requests'] for row in cells):
                    means[route] = [self.a21.statistics.mean(record['completion_ns'] - record['scheduled_ns']
                        for record in row['records']) for row in cells]
            if name not in self.bands or len(means) != 3:
                result[name] = {'decision': 'inconclusive', 'reason': 'No matched three-route successful cohort or frozen measured band'}
                continue
            band = dict(self.bands[name], baseline_id=self.calibration['fingerprint'])
            result[name] = {route: self.a21.noise_comparison(means['direct-rust'], means[route], band, self.profile['seed'])
                            for route in sorted(self.a21.ROUTES - {'direct-rust'})}
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--phase', choices=['functional', 'calibrate', 'measure'], default='functional')
    parser.add_argument('--package-probe', required=True, type=Path)
    parser.add_argument('--comparators', type=Path)
    parser.add_argument('--instrumented', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--calibration', type=Path)
    parser.add_argument('--cases', choices=['primary', 'extended', 'diagnostics', 'all'], default='primary')
    parser.add_argument('--select', help='Comma-separated workload IDs or full workload/provider/protocol IDs')
    parser.add_argument('--profile', type=Path, default=HERE / 'profile-v1.json')
    parser.add_argument('--a21', type=Path, default=ROOT / 'src/Benchmarks.Harness/nativeweave_bench.py')
    parser.add_argument('--golden', type=Path, default=ROOT / 'tests/Reference.TaskService/golden.py')
    parser.add_argument('--postgres-bin', type=Path)
    parser.add_argument('--isolation-note', default='Functional validation; no performance conclusion')
    args = parser.parse_args()
    if args.phase != 'functional' and args.isolation_note == 'Functional validation; no performance conclusion':
        parser.error('Timed runs require --isolation-note identifying the window separated from B22 fault/soak load')
    output_existed = args.output.exists()
    try:
        controller = Controller(args)
    except Exception as error:
        if not output_existed and args.output.is_dir():
            save(args.output / 'failure.json', {'stage': 'preflight', 'phase': args.phase,
                'error': type(error).__name__ + ': ' + str(error), 'controller_sha256': digest(__file__),
                'claim': 'No workload was executed; this is not a benchmark result'})
        raise
    if any(case['provider'] == 'postgresql' for case in controller.cases):
        if not args.postgres_bin:
            parser.error('PostgreSQL cells require --postgres-bin')
        with postgres(controller.output / 'postgres', args.postgres_bin) as settings:
            return asyncio.run(controller.run(settings))
    return asyncio.run(controller.run({}))


if __name__ == '__main__':
    raise SystemExit(main())
