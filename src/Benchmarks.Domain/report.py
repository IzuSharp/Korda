#!/usr/bin/env python3
"""Render B23 raw A21 observations without replacing their validity decisions."""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import tarfile

from common import digest, load, load_a21, save
from engineering_cost import inventory


def number(value, divisor=1, digits=3):
    return 'unavailable' if value is None else f'{value / divisor:.{digits}f}'


def observation_value(sample, name):
    value = sample.get('counters', {}).get(name, {})
    return value.get('value') if value.get('availability') == 'measured' else None


def app_costs(receipt):
    before = receipt.get('measurement_resources_before', {})
    after = receipt.get('measurement_resources_after', {})
    cpu = None
    if before.get('cpu_ticks') is not None and after.get('cpu_ticks') is not None:
        cpu = (after['cpu_ticks'] - before['cpu_ticks']) / after['clock_ticks_per_second']
    probes = receipt.get('probe_before'), receipt.get('probe_after')
    allocation = None
    gc = None
    if all(probes):
        allocation = probes[1]['allocatedBytes'] - probes[0]['allocatedBytes']
        gc = [probes[1][name] - probes[0][name] for name in ('gen0Collections', 'gen1Collections', 'gen2Collections')]
    return {'application_cpu_seconds': cpu,
        'rss_before_bytes': observation_value(before, 'rss_bytes'),
        'rss_after_bytes': observation_value(after, 'rss_bytes'),
        'lifetime_peak_rss_bytes': observation_value(after, 'peak_rss_bytes'),
        'private_working_set_after_bytes': observation_value(after, 'private_working_set_bytes'),
        'threads_after': observation_value(after, 'threads'), 'fds_after': after.get('fd_count'),
        'sockets_after': after.get('socket_count'), 'managed_allocated_bytes_delta': allocation,
        'gc_collection_deltas': gc, 'probe_before': probes[0], 'probe_after': probes[1],
        'startup_to_ready_ns': receipt.get('startup_to_ready_ns'),
        'image_mappings_after': receipt.get('image_mappings_after'),
        'native_allocator_live_bytes': None, 'native_allocator_retained_bytes': None,
        'ffi_calls': None, 'copy_bytes': None,
        'availability_note': 'Unavailable allocation/copy/FFI/allocator counters stay null. RSS is not native allocation; native payload gauges are not whole-process memory.'}


def report(directory, a21):
    summary = load(directory / 'summary.json')
    grouped = {}
    raw = []
    for entry in summary.get('runs', []):
        path = directory / entry['path']
        if digest(path) != entry['sha256']:
            raise ValueError('Raw observation changed after the phase completed: ' + str(path))
        row = load(path)
        receipt = None
        if row.get('application_receipt'):
            reference = row['application_receipt']
            reference_path = Path(reference['path'])
            if not reference_path.is_file():
                reference_path = directory / 'applications' / reference_path.parent.name / 'receipt.json'
            if digest(reference_path) != reference['sha256']:
                raise ValueError('Application oracle receipt changed')
            receipt = load(reference_path)
        raw.append({'case': row['case'], 'route': row['route'], 'repetition': row['repetition'],
            'valid': row['valid'], 'invalid_reasons': row.get('invalid_reasons', []),
            'application_costs': app_costs(receipt) if receipt else None,
            'http_status_counts_including_setup': receipt.get('http_status_counts') if receipt else None,
            'http_error_codes_including_setup': receipt.get('http_error_codes') if receipt else None,
            'durable_oracle': receipt.get('durable_oracle') if receipt else None})
        grouped.setdefault((row['case'], row['route']), []).append((row, receipt))
    groups = []
    for (case, route), cells in sorted(grouped.items()):
        valid = [row for row, _ in cells if row['valid']]
        records = [record for row in valid for record in row.get('records', [])]
        latencies = [record['completion_ns'] - record['scheduled_ns'] for record in records]
        scale = valid[0]['histograms']['arrival_all_outcomes'] if valid else None
        histogram = a21.histogram(latencies, scale['resolution_ns'], scale['maximum_ns']) if latencies else None
        stages = {}
        for label, start, end, successful in [('dispatch_delay', 'scheduled_ns', 'dispatch_ns', False),
                ('client_queue_and_admission', 'dispatch_ns', 'admission_ns', False),
                ('service_observed_success', 'admission_ns', 'completion_ns', True)]:
            values = [record[end] - record[start] for record in records
                if record.get(start) is not None and record.get(end) is not None and (not successful or record['status'] == 'ok')]
            stages[label] = a21.histogram(values, scale['resolution_ns'], scale['maximum_ns']) if values else None
        means = [a21.statistics.mean(record['completion_ns'] - record['scheduled_ns'] for record in row['records']) for row in valid]
        elapsed = sum(row['measurement_window_ns']['elapsed'] for row in valid)
        costs = [app_costs(receipt) for row, receipt in cells if row['valid'] and receipt]
        def mean_cost(key):
            values = [cost[key] for cost in costs if cost[key] is not None]
            return a21.statistics.mean(values) if values else None
        groups.append({'case': case, 'route': route, 'repetitions': len(cells), 'valid_repetitions': len(valid),
            'samples': len(records), 'outcomes': dict(Counter(record['status'] for record in records)),
            'process_mean_arrival_ns': means, 'histogram': histogram, 'stage_histograms': stages,
            'maximum_arrival_ns': max(latencies) if latencies else None,
            'offered_rate_per_second': valid[0]['rates_per_second']['offered'] if valid else None,
            'admitted_rate_per_second': sum(row['admitted'] for row in valid) * 1e9 / elapsed if elapsed else None,
            'completed_rate_per_second': sum(row['completed'] for row in valid) * 1e9 / elapsed if elapsed else None,
            'mean_application_cpu_seconds': mean_cost('application_cpu_seconds'),
            'mean_startup_to_ready_ns': mean_cost('startup_to_ready_ns'),
            'application_cpu_seconds_per_offered_operation':
                sum(cost['application_cpu_seconds'] for cost in costs) / len(records)
                if records and all(cost['application_cpu_seconds'] is not None for cost in costs) else None,
            'mean_rss_after_bytes': mean_cost('rss_after_bytes'),
            'mean_private_working_set_after_bytes': mean_cost('private_working_set_after_bytes'),
            'mean_threads_after': mean_cost('threads_after'),
            'mean_managed_allocated_bytes_delta': mean_cost('managed_allocated_bytes_delta'),
            'managed_allocated_bytes_per_offered_operation':
                sum(cost['managed_allocated_bytes_delta'] for cost in costs) / len(records)
                if records and all(cost['managed_allocated_bytes_delta'] is not None for cost in costs) else None,
            'validity': 'all repetitions valid' if len(valid) == len(cells) else 'invalid repetitions retained; no complete-cohort performance conclusion'})
    diagnostic_comparisons = {}
    di_comparison = None
    baseline_name = 'diagnostics-release-disabled-sqlite-http1'
    baseline = next((group for group in groups if group['case'] == baseline_name and group['route'] == 'nativeweave-aot-rust'), None)
    if summary['phase'] == 'measure' and baseline and baseline['repetitions'] == baseline['valid_repetitions']:
        for group in groups:
            if not group['case'].startswith('diagnostics-') or group['case'] == baseline_name:
                continue
            if group['case'].startswith('diagnostics-exporter-saturation'):
                diagnostic_comparisons[group['case']] = {'decision': 'not_comparable',
                    'reason': 'Longer declared saturation workload has different request count; report raw loss/resource observations, not a latency superiority claim'}
                continue
            bands = summary.get('noise_bands', {})
            if group['case'] not in bands or baseline_name not in bands or group['repetitions'] != group['valid_repetitions']:
                continue
            band = {'baseline_id': baseline_name,
                'absolute_noise_ns': max(bands[baseline_name]['absolute_noise_ns'], bands[group['case']]['absolute_noise_ns']), 'relative_noise': 0}
            diagnostic_comparisons[group['case']] = a21.noise_comparison(baseline['process_mean_arrival_ns'],
                group['process_mean_arrival_ns'], band, 20261118)
    if summary['phase'] == 'measure':
        minimal = next((group for group in groups if group['case'] == 'minimal-list-sqlite-http1'), None)
        advanced = next((group for group in groups if group['case'] == 'advanced-di-list-sqlite-http1'), None)
        bands = summary.get('noise_bands', {})
        if minimal and advanced and all(group['repetitions'] == group['valid_repetitions'] and group['case'] in bands for group in (minimal, advanced)):
            band = {'baseline_id': minimal['case'], 'absolute_noise_ns': max(bands[group['case']]['absolute_noise_ns']
                    for group in (minimal, advanced)), 'relative_noise': 0}
            di_comparison = a21.noise_comparison(minimal['process_mean_arrival_ns'], advanced['process_mean_arrival_ns'], band, 20261118)
    return {'schema_version': 1, 'task': 'B23', 'phase': summary['phase'],
        'source_summary': {'path': str(directory / 'summary.json'), 'sha256': digest(directory / 'summary.json')},
        'state': summary['state'], 'claim': summary['claim'], 'acceptance_gate': summary['acceptance_gate'],
        'host': summary['host'], 'isolation_note': summary['isolation_note'],
        'routes': summary['routes'], 'unsupported': summary['unsupported'], 'groups': groups,
        'independent_process_observations': raw, 'comparisons': summary.get('comparisons'),
        'diagnostic_comparisons': diagnostic_comparisons, 'di_comparison': di_comparison, 'noise_bands': summary.get('noise_bands'),
        'statistics_authority': 'Unmodified A21 histogram, nearest-rank sample thresholds, independent-process bootstrap and validity decisions',
        'tail_note': 'p99.9 is unavailable below 1000 samples; aggregate percentiles describe pooled raw requests, confidence intervals compare independent process means',
        'counter_note': 'App child procfs and standard GC/Meter observations are separate from Python adapter resources. Unknown counters are not zero.'}


def markdown(value):
    lines = ['# B23 domain workload observations', '',
        f"Phase: **{value['phase']}**. Execution state: **{value['state']}**.", '',
        value['claim'] + '.', '', '**Acceptance:** ' + value['acceptance_gate'] + '.', '',
        '## Scope and reproducibility', '',
        'The product is the accepted C# reference application over the unchanged Rust Database and Server engines. '
        'The direct Rust and managed Native AOT applications are comparison executables. '
        'Their exact source, lock, executable, native and configuration hashes accompany the raw phase records.', '',
        'Execution isolation: ' + value['isolation_note'] + '.', '',
        'A21 schedules independent arrivals and retains generator lag, all outcomes, raw events and histograms. '
        'The HTTP adapter validates actual protocol/TLS and the application AOT witness; its separate SQL/blob oracle checks durable effects after each process cohort. '
        'Adapter admission is HTTP-client dispatch, not an internal Server or database-pool admission timestamp.', '',
        '## Workload results', '',
        '| Workload / route | Valid processes | Samples / outcomes | Offered / admitted / completed per second | p50 / p95 / p99 / p99.9 (ms) | Max (ms) |',
        '| --- | ---: | --- | --- | --- | ---: |']
    for group in value['groups']:
        percentiles = group['histogram']['percentiles_from_exact_samples_ns'] if group['histogram'] else {}
        latency = ' / '.join(number(percentiles.get(key), 1e6) for key in ('p50', 'p95', 'p99', 'p99.9'))
        rates = ' / '.join(number(group.get(key)) for key in ('offered_rate_per_second', 'admitted_rate_per_second', 'completed_rate_per_second'))
        lines.append(f"| {group['case']} / {group['route']} | {group['valid_repetitions']}/{group['repetitions']} | {group['samples']} / {json.dumps(group['outcomes'], sort_keys=True)} | {rates} | {latency} | {number(group['maximum_arrival_ns'], 1e6)} |")
    lines += ['', value['tail_note'] + '.', '',
        'Expected HTTP500 diagnostic probes form separate workloads. Logical oracle success means the declared result matched; it never relabels those HTTP errors as business success. '
        'Individual process receipts preserve actual HTTP status counts, including setup/warmup, separately from measured logical outcomes.', '',
        '## Latency stages and startup', '',
        '| Workload / route | Startup to READY (ms) | Dispatch delay p95 (ms) | Client queue/admission p95 (ms) | Successful service observed p95 (ms) |',
        '| --- | ---: | ---: | ---: | ---: |']
    for group in value['groups']:
        stages = group['stage_histograms']
        percentiles = [stages[name]['percentiles_from_exact_samples_ns']['p95'] if stages[name] else None
                      for name in ('dispatch_delay', 'client_queue_and_admission', 'service_observed_success')]
        lines.append(f"| {group['case']} / {group['route']} | {number(group['mean_startup_to_ready_ns'], 1e6)} | "
            + ' | '.join(number(item, 1e6) for item in percentiles) + ' |')
    lines += ['', 'Queue/admission is observed over the controller/adapter IPC and includes the bounded HTTP-client queue. '
        'Service duration includes HTTP, application, database and client work after adapter admission; it is not an internal database execution timer. '
        'Raw A21 records also retain attempts and protocol-event bytes. Those JSONL byte counts are not HTTP/TLS wire-byte measurements.', '',
        '## Application memory and CPU', '',
        '| Workload / route | CPU per offered operation (ms) | RSS after (MiB) | Private working set after (MiB) | Threads after | Managed allocated bytes per offered operation |',
        '| --- | ---: | ---: | ---: | ---: | ---: |']
    for group in value['groups']:
        lines.append(f"| {group['case']} / {group['route']} | {number(group['application_cpu_seconds_per_offered_operation'], .001)} | "
            f"{number(group['mean_rss_after_bytes'], 1048576)} | {number(group['mean_private_working_set_after_bytes'], 1048576)} | "
            f"{number(group['mean_threads_after'], digits=1)} | {number(group['managed_allocated_bytes_per_offered_operation'], digits=0)} |")
    lines += ['', value['counter_note'] + '.', '',
        'CPU comes from child-process procfs ticks around measured requests. RSS/private working set are endpoint observations, '
        'and lifetime high-water RSS additionally remains in the JSON. Native allocator live/retained bytes, per-operation FFI/copy counts, '
        'whole-thread stacks and kernel-buffer memory remain unavailable where no supported observer exists. '
        'A21 raw files separately retain the adapter process observations; those are never presented as application memory.', '',
        'Instrumented application variants use standard GC totals and Activity/Meter/ILogger listeners. '
        'GC deltas include the observation boundary overhead and any concurrent application allocations; they are not isolated DTO allocations. '
        'Native payload gauges describe retained charged payload owners, not all native allocations or a process RSS bound. '
        'Meter callback snapshots have the accepted one-second cache interval. Instrumented cells allow a declared 1.1-second observation settling interval after measurement boundaries. '
        'Module mappings preserve virtual image/code bytes separately from resident memory. Process exit alone is not a retirement or leak proof.', '',
        '## Actual diagnostic observations', '',
        '| Workload / process | Activities (delta) | Log calls / exports / failures (delta) | Callback max (us) | Dropped metadata (delta) | Queue entries / bytes sampled peak | Managed / native payload sampled peak (bytes) |',
        '| --- | ---: | --- | ---: | ---: | --- | --- |']
    for row in value['independent_process_observations']:
        cost = row.get('application_costs') or {}
        before, after = cost.get('probe_before'), cost.get('probe_after')
        if not before or not after:
            continue
        metrics, peaks = after.get('metrics') or {}, after.get('metricPeaks') or {}
        initial = before.get('metrics') or {}
        delta = lambda name: after[name] - before[name]
        drops = 'nativeweave.server.diagnostics.dropped'
        lost = metrics[drops] - initial[drops] if drops in metrics and drops in initial else None
        queue = ' / '.join(number(peaks.get('nativeweave.server.diagnostics.queue.' + name), digits=0) for name in ('entries', 'bytes'))
        payload = ' / '.join(number(peaks.get('nativeweave.server.payload.' + name + '.bytes'), digits=0) for name in ('managed', 'native'))
        lines.append(f"| {row['case']} / {row['repetition']} | {delta('activities')} | "
            + ' / '.join(str(delta(name)) for name in ('logCalls', 'logExports', 'exportFailures'))
            + f" | {number(metrics.get('nativeweave.server.callback.duration.max'), .000001)} | {number(lost, digits=0)} | {queue} | {payload} |")
    lines += ['', 'Sampled peaks include setup and observation endpoints and can miss shorter peaks between observations. '
        'No-listener profiles leave callback/drop/native-payload observations unavailable instead of enabling a Meter implicitly. '
        'Exporter saturation and failure are actual controlled sink behavior; the saturation row must exceed the 2048-entry queue and observe a positive drop count. '
        'Reduced functional runs only check blocking configuration and do not claim the queue filled.', '',
        '## Measured-noise comparisons', '']
    comparisons = value.get('comparisons') or {}
    if not comparisons:
        lines += ['No confirmatory route comparison was requested in this phase.', '']
    for name, routes in comparisons.items():
        lines.append('### ' + name)
        lines.append('')
        if 'decision' in routes:
            lines.append(routes['decision'] + ': ' + routes.get('reason', ''))
        else:
            for route, comparison in routes.items():
                interval = comparison.get('bootstrap_95_percent_interval_ns')
                lines.append(f"- {route}: {comparison['decision']}; effect {number(comparison.get('effect_ns'), 1e6)} ms; "
                    f"95% bootstrap interval {(' to '.join(number(item, 1e6) for item in interval)) if interval else 'unavailable'} ms; "
                    f"measured noise band {number(comparison.get('noise_band_ns'), 1e6)} ms.")
        lines.append('')
    if value['diagnostic_comparisons']:
        lines += ['### Diagnostic configurations', '', 'Comparisons use disabled Release as the actual baseline and the larger frozen calibration band of the two profiles.', '']
        for name, comparison in value['diagnostic_comparisons'].items():
            lines.append('- ' + name + ': ' + json.dumps(comparison, sort_keys=True))
        lines.append('')
    if value['di_comparison']:
        lines += ['### Minimal and request-DI application paths', '',
            'Both paths use the same copied executable, disabled diagnostics, static JSON and 128-row typed query. '
            'The advanced handler resolves the registered service through request DI; the minimal handler captures it.', '',
            json.dumps(value['di_comparison'], sort_keys=True), '']
    lines += ['Bands are derived from independent calibration-process variability before confirmation data is collected. '
        'A21 supplies the seeded process-bootstrap intervals. This initial baseline has no historical-release performance target; '
        'a route-cost decision does not select or replace the product engine. Small process counts limit interval precision.', '',
        '## Deployment and developer cost', '',
        '| Route | Runtime deployment (MiB) | Executable (MiB) | Native modules (MiB) | Debug symbols (MiB) |',
        '| --- | ---: | ---: | ---: | ---: |']
    engineering = value.get('engineering_cost', {})
    source_notes = []
    for name, route in engineering.get('routes', {}).items():
        roles = route['bytes_by_role']
        lines.append(f"| {name} | {number(route['runtime_deployment_bytes'], 1048576)} | "
            + ' | '.join(number(roles.get(role, 0), 1048576) for role in ('runtime-executable', 'native-module', 'debug-symbol')) + ' |')
        application = route.get('application', {})
        if 'application_variants' in application:
            counts = application['application_variants']['baseline']['physical_lines_by_role']
            source_notes.append(f"- {name}: application source physical lines by role {json.dumps(counts, sort_keys=True)}; "
                f"compressed NativeWeave package closure {number(application['compressed_nativeweave_package_bytes'], 1048576)} MiB.")
        else:
            source_notes.append(f"- {name}: application source physical lines by role {json.dumps(application.get('physical_lines_by_role', {}), sort_keys=True)}.")
    lines += ['', *source_notes]
    lines += ['', engineering.get('optional_protocol_closure', 'Engineering inventory was not supplied.'), '',
        'Source line counts include comments/blanks and retain individual hashes. They cover the named app/comparator sources only; '
        'dependencies, precompiled Rust modules and unpersisted source-generator output are disclosed separately. '
        'Code moved into a package or generated layer is not treated as eliminated complexity.', '',
        '| Recorded application build | Command | Elapsed (s) | Observation method |', '| --- | --- | ---: | --- |']
    builds = []
    for name, route in engineering.get('routes', {}).items():
        builds += [(name, command) for command in route.get('application', {}).get('recorded_build_commands', [])]
        builds += [(name, command) for command in route.get('recorded_build_commands', [])]
    builds += [('ordinary-edit-evidence', command) for command in (engineering.get('ordinary_edit') or {}).get('recorded_build_commands', [])]
    builds += [('instrumented-copy', command) for command in engineering.get('instrumented_build', {}).get('commands', [])]
    for name, command in builds:
        lines.append(f"| {name} | {command['label']} | {number(command['elapsed_seconds'])} | {command['timing_method']} |")
    edit = engineering.get('ordinary_edit')
    if edit:
        lines += ['', 'Accepted ordinary Entity/DTO/Handler edit evidence: ' + json.dumps(edit['ordinary_edit_deltas'], sort_keys=True) + '.', '']
    lines += ['Build durations reuse actual recorded commands with warm/available caches. Missing durations remain unavailable; '
        'these observations are not a clean-machine build-time comparison. The full exact command/source/native/package inventory is in the JSON.', '',
        '## Unsupported and non-equivalent cells', '']
    if value['unsupported']:
        for item in value['unsupported']:
            lines.append(f"- {item['case']} / {item['route']}: {item['reason']}.")
    else:
        lines.append('No selected route cell was omitted as unsupported. Route architecture limitations still apply.')
    for name, route in value['routes'].items():
        for limitation in route.get('limitations', []):
            lines.append(f'- {name}: {limitation}.')
    lines += ['', '## Interpretation and remaining gates', '',
        'The measured reference service includes typed query construction, Rust database execution, DTO serialization, authenticated HTTP, '
        'transactional result/outbox writes, keyset queries and attachment staging/streaming in the declared cells. '
        'Foreground timing disables the periodic outbox consumer equally; external publication throughput is not established by these rows. '
        'Content-addressed stream fixtures reuse identical bytes after their first upload, explicitly distinguishing that cache profile from unique-content storage growth.', '',
        'Cold means a fresh application process/database and its first request. Host page caches are not flushed. '
        'The fixed native Server closure still includes its accepted optional protocol code even when only REST is configured; '
        'no absent-code or zero-cost claim follows from an unconfigured facade.', '',
        'Unsupported query families or optional protocol facades absent from the reference app are not silently represented by an empty handler. '
        'No unmeasured optimization or universal percentage benefit is asserted. B22 supplies independent fault/long-duration correctness, '
        'and B24 supplies final package/pristine-consumer/release acceptance. Linux evidence does not certify other platforms.', '']
    return '\n'.join(lines)


def archive(directory, destination):
    """Portable raw evidence; private runtime configs/keys/databases are excluded."""
    patterns = ['summary.json', 'frozen-noise-bands.json', 'failure.json', 'plans/*/*.json',
                'observations/*.json', 'observations/*.jsonl', 'applications/*/receipt.json',
                'applications/*/application/*.log', 'applications/*/application/resources.jsonl',
                'postgres/identity.json', 'postgres/commands.json', 'postgres/command-*.log']
    selected = sorted({path for pattern in patterns for path in directory.glob(pattern) if path.is_file()})
    with tarfile.open(destination, 'w:gz') as bundle:
        for path in selected:
            bundle.add(path, arcname=str(path.relative_to(directory)), recursive=False)
    return {'path': str(destination), 'sha256': digest(destination), 'files': len(selected), 'bytes': destination.stat().st_size}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase_directory', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--archive', action='store_true')
    parser.add_argument('--edit-probe', type=Path, help='Accepted B21 baseline/edited-consumer progress directory')
    parser.add_argument('--instrumented', type=Path, help='Copied-app instrumentation build manifest')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    value = report(args.phase_directory.resolve(), load_a21())
    value['engineering_cost'] = inventory(value['routes'], args.edit_probe, args.instrumented)
    value['analysis_source_sha256'] = {str(path): digest(path) for path in (Path(__file__).resolve(), Path(__file__).with_name('engineering_cost.py'))}
    if args.archive:
        value['raw_archive'] = archive(args.phase_directory.resolve(), args.output / 'raw-evidence.tar.gz')
    save(args.output / 'report.json', value)
    (args.output / 'report.md').write_text(markdown(value))
    print(args.output / 'report.md')


if __name__ == '__main__':
    main()
