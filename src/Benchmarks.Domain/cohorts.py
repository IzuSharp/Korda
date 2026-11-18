#!/usr/bin/env python3
"""Finite B23 operating-point search and independent per-case cohorts.

This orchestrator invokes run.py unchanged for all measurement and validity
decisions. It adds no clock, histogram, performance threshold or retry policy.
Interrupted directories and invalid cases remain evidence, never successful runs.
"""
from __future__ import annotations

import argparse
import copy
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import random
import subprocess
import sys

from common import check_files, digest, load, load_a21, save
import report as rendering
from run import case_id, select_cases

HERE = Path(__file__).resolve().parent


def utc():
    return datetime.now(timezone.utc).isoformat()


def adjusted(profile, family, rate, seconds):
    result = copy.deepcopy(profile)
    for workload in result['workloads']:
        if workload['id'] in family['applies_to']:
            selected = min(rate, family.get('low_concurrency_rate_cap', rate)) \
                if workload['id'] == 'warm-cycle-low' else rate
            workload.update(rate=selected, requests=round(selected * seconds))
    if 'minimal-list' in family['applies_to']:
        result['di_workload'] = {'rate': rate, 'requests': round(rate * seconds)}
    return result


def prepare(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    plan = load(args.plan)
    original = load(HERE / plan['original_profile'])
    files = {str(path): digest(path) for path in HERE.glob('*.py')}
    for path in (args.plan.resolve(), HERE / plan['original_profile'],
                 args.package_probe.resolve() / 'progress.json', args.comparators.resolve(),
                 args.instrumented.resolve(), HERE.parent / 'Benchmarks.Harness/nativeweave_bench.py'):
        files[str(path)] = digest(path)
    external = [HERE.parent.parent / 'tests/Reference.TaskService/golden.py', Path(sys.executable).resolve()]
    external.extend(args.package_probe.resolve() / 'tls' / name for name in ('localhost.pem','localhost.private.pem'))
    external.extend(args.postgres_bin.resolve() / name for name in ('postgres','psql','initdb','pg_ctl'))
    external.extend(Path(importlib.util.find_spec(name).origin).resolve()
                    for name in ('httpx','httpcore','h11','h2','hpack','hyperframe'))
    for path in external:
        files[str(path)] = digest(path)
    units = []
    for family in plan['families']:
        for rate in family['rates_per_second']:
            name = family['id'] + '-' + str(rate)
            profile = adjusted(original, family, rate, plan['scheduled_span_seconds'])
            profile['calibration_repetitions'] = plan['search_repetitions']
            path = output / 'profiles' / (name + '.json')
            save(path, profile)
            files[str(path)] = digest(path)
            units.append({'id': name, 'family': family['id'], 'rate': rate,
                          'profile': str(path), 'select': ','.join(family['search_cases'])})
    random.Random(plan['seed']).shuffle(units)
    state = {'schema_version': 1, 'task': 'B23', 'created_utc': utc(),
        'plan': plan, 'frozen_files': files, 'search_units': units,
        'inputs': {'package_probe': str(args.package_probe.resolve()),
                   'comparators': str(args.comparators.resolve()),
                   'instrumented': str(args.instrumented.resolve()),
                   'postgres_bin': str(args.postgres_bin.resolve()),
                   'isolation_note': args.isolation_note},
        'search': {}, 'matrix': {}, 'expected_cases': [case_id(case) for case in select_cases(original, 'all')],
        'state': 'prepared-no-measurements'}
    save(output / 'cohorts.json', state)
    print(json.dumps({'state': state['state'], 'output': str(output), 'search_units': len(units)}))


def command(state, output, profile, selected, phase, calibration=None):
    result = [sys.executable, str(HERE / 'run.py'), '--phase', phase, '--cases', 'all',
              '--profile', str(profile), '--select', selected, '--output', str(output)]
    for name, value in state['inputs'].items():
        result.extend(['--' + name.replace('_', '-'), value])
    if calibration is not None:
        result.extend(['--calibration', str(calibration)])
    return result


def execute(state, output, arguments):
    """A separate invocation is never retried into an existing evidence directory."""
    check_files(state['frozen_files'])
    if output.exists():
        return {'state': 'interrupted-existing-directory', 'valid': False,
                'path': str(output), 'reason': 'Preserved interrupted invocation; no retry or inferred completion',
                'execution_blocker': 'Cleanup of the prior interrupted invocation is not established'}
    output.parent.mkdir(parents=True, exist_ok=True)
    log = output.with_suffix('.command.log')
    record = {'argv': arguments, 'started_utc': utc(), 'path': str(output), 'log': str(log),
              'state': 'running', 'valid': False}
    try:
        with log.open('w') as handle:
            result = subprocess.run(arguments, stdout=handle, stderr=subprocess.STDOUT, check=False)
        record['exit_code'] = result.returncode
        record['ended_utc'] = utc()
        record['log_sha256'] = digest(log)
        summary_path = output / 'summary.json'
        if summary_path.is_file():
            summary = load(summary_path)
            record['summary_sha256'] = digest(summary_path)
            record['state'] = summary['state']
            record['valid'] = (result.returncode == 0 and summary.get('all_selected_cells_executed') is True
                               and summary.get('all_executed_cells_valid') is True)
            record['processes'] = len(summary['runs'])
            record['invalid_processes'] = sum(not row['valid'] for row in summary['runs'])
            record['execution_blocker'] = summary.get('execution_blocker')
            record['stop_requested'] = summary.get('stop_requested', False)
            record['not_run'] = summary.get('not_run', [])
            if summary['state'] == 'running':
                record['execution_blocker'] = 'Invocation ended without a terminal summary; cleanup is not established'
        else:
            record['state'] = 'preflight-failed'
            if not (output / 'failure.json').is_file():
                record['execution_blocker'] = 'Invocation ended without a preflight or terminal receipt; cleanup is not established'
        check_files(state['frozen_files'])
        return record
    except BaseException:
        record['state'] = 'interrupted'
        record['ended_utc'] = utc()
        save(output.with_suffix('.interrupted.json'), record)
        raise


def halted(output, state, record):
    if not record.get('execution_blocker') and not record.get('stop_requested'):
        return False
    state['state'] = 'blocked-further-invocations'
    state['execution_blocker'] = {'source': record['path'],
        'reason': record.get('execution_blocker') or 'Explicit stop requested in the active controller',
        'source_summary_sha256': record.get('summary_sha256')}
    state['not_run'] = {'search_units': [unit['id'] for unit in state['search_units']
                                        if unit['id'] not in state['search']],
        'matrix_cases': [name for name in state['expected_cases'] if not state['matrix'].get(name)],
        'confirmation_cases': [name for name in state['expected_cases']
                               if 'confirmation' not in state['matrix'].get(name, {})]}
    save(output / 'cohorts.json', state)
    return True


def search(output, state):
    for unit in state['search_units']:
        if unit['id'] in state['search']:
            continue
        destination = output / 'search' / unit['id']
        print('SEARCH ' + unit['id'], flush=True)
        state['active'] = {'phase': 'search', 'unit': unit['id']}
        save(output / 'cohorts.json', state)
        state['search'][unit['id']] = execute(state, destination,
            command(state, destination, unit['profile'], unit['select'], 'calibrate'))
        save(output / 'cohorts.json', state)
        if halted(output, state, state['search'][unit['id']]):
            return
    plan = state['plan']
    profile = load(HERE / plan['original_profile'])
    selected = []
    for family in plan['families']:
        passed = [unit['rate'] for unit in state['search_units']
                  if unit['family'] == family['id'] and state['search'][unit['id']]['valid']]
        rate = max(passed) if passed else min(family['rates_per_second'])
        selected.append({'family': family['id'], 'rate': rate,
                         'search_validated': bool(passed),
                         'claim': 'finite operating point; not maximum sustainable capacity'})
        profile = adjusted(profile, family, rate, plan['scheduled_span_seconds'])
    profile['repetitions'] = profile['calibration_repetitions'] = plan['confirmation_repetitions']
    profile['operating_point_selection'] = selected
    destination = output / 'profiles' / 'selected.json'
    save(destination, profile)
    state['frozen_files'][str(destination)] = digest(destination)
    state['selected_profile'] = str(destination)
    state['selection'] = selected
    state['matrix_order'] = [case_id(case) for case in select_cases(profile, 'all')]
    random.Random(plan['seed'] + 2).shuffle(state['matrix_order'])
    state['state'] = 'selected-profile-frozen-before-final-calibration'
    state.pop('active', None)
    save(output / 'cohorts.json', state)


def matrix(output, state):
    if not state.get('selected_profile'):
        raise ValueError('Complete the finite search and freeze its selected profile first')
    for name in state['matrix_order']:
        entry = state['matrix'].setdefault(name, {})
        if entry.get('state') in ('completed-confirmation', 'retained-adverse-calibration', 'retained-incomplete-confirmation'):
            continue
        state['active'] = {'phase': 'matrix', 'case': name}
        save(output / 'cohorts.json', state)
        calibration = output / 'matrix' / name / 'calibration'
        if 'calibration' not in entry:
            print('CALIBRATE ' + name, flush=True)
            entry['calibration'] = execute(state, calibration,
                command(state, calibration, state['selected_profile'], name, 'calibrate'))
            save(output / 'cohorts.json', state)
            if halted(output, state, entry['calibration']):
                return
        if not entry['calibration']['valid']:
            entry['state'] = 'retained-adverse-calibration'
            entry['confirmation'] = {'state': 'not-run', 'reason': 'Incomplete or invalid calibration; no matching valid frozen band'}
        else:
            summary = calibration / 'summary.json'
            if digest(summary) != entry['calibration']['summary_sha256']:
                raise ValueError('Completed calibration changed before confirmation')
            destination = output / 'matrix' / name / 'confirmation'
            print('CONFIRM ' + name, flush=True)
            entry['confirmation'] = execute(state, destination,
                command(state, destination, state['selected_profile'], name, 'measure', calibration))
            entry['state'] = ('completed-confirmation' if entry['confirmation']['state'] in
                ('completed', 'completed-with-invalid-cells') else 'retained-incomplete-confirmation')
            if halted(output, state, entry['confirmation']):
                return
        save(output / 'cohorts.json', state)
    terminal = ('completed', 'completed-with-invalid-cells')
    entries = list(state['matrix'].values())
    counts = {'declared_cases': len(state['matrix_order']),
        'calibrations_attempted': sum('calibration' in entry for entry in entries),
        'calibrations_completed': sum(entry.get('calibration',{}).get('state') in terminal for entry in entries),
        'valid_calibrations': sum(entry.get('calibration',{}).get('valid',False) for entry in entries),
        'confirmations_attempted': sum('path' in entry.get('confirmation',{}) for entry in entries),
        'confirmations_completed': sum(entry.get('confirmation',{}).get('state') in terminal for entry in entries),
        'valid_confirmations': sum(entry.get('confirmation',{}).get('valid',False) for entry in entries)}
    state['counts'] = counts
    complete = (counts['calibrations_completed'] == counts['declared_cases']
                and counts['confirmations_completed'] == counts['valid_calibrations'])
    state['state'] = 'complete-with-adverse-cells-preserved' if complete else 'incomplete-attempts-retained'
    state.pop('active', None)
    save(output / 'cohorts.json', state)


def write_report(output, state):
    a21 = load_a21()
    reports = []
    for name in state.get('matrix_order', []):
        entry = state['matrix'].get(name, {})
        label = 'confirmation' if entry.get('confirmation', {}).get('path') else 'calibration'
        observation = entry.get(label, {})
        if not observation.get('summary_sha256'):
            reports.append({'case': name, 'phase': 'not-completed', 'state': entry})
            continue
        directory = Path(observation['path'])
        if digest(directory / 'summary.json') != observation['summary_sha256']:
            raise ValueError('Completed cohort summary changed')
        value = rendering.report(directory, a21)
        reports.append({'case': name, 'phase': label, 'state': entry['state'], 'report': value})
    confirmed = {case['case']: case['report'] for case in reports
                 if case['phase'] == 'confirmation' and case.get('report')}
    def compare(left, right):
        if left not in confirmed or right not in confirmed:
            return {'decision': 'inconclusive', 'reason': 'Both independent confirmation cohorts are required'}
        values = [confirmed[name] for name in (left, right)]
        groups = [next((group for group in value['groups'] if group['route'] == 'nativeweave-aot-rust'), None)
                  for value in values]
        if any(not group or not group['complete_valid_cohort'] for group in groups):
            return {'decision': 'inconclusive', 'reason': 'Invalid or incomplete confirmation process cohort'}
        bands = [value['noise_bands'].get(name) for name, value in zip((left,right), values)]
        if not all(bands):
            return {'decision': 'inconclusive', 'reason': 'Both independently frozen calibration bands are required'}
        band = {'baseline_id': left, 'absolute_noise_ns': max(item['absolute_noise_ns'] for item in bands), 'relative_noise': 0}
        return a21.noise_comparison(groups[0]['process_mean_arrival_ns'], groups[1]['process_mean_arrival_ns'],
                                    band, state['plan']['seed'])
    diagnostic = {}
    for name in load(state['selected_profile'])['diagnostics_profiles']:
        if name == 'release-disabled':
            continue
        diagnostic[name] = ({'decision': 'not_comparable', 'reason': 'Longer2300-event workload fills the actual bounded exporter queue; raw loss/resources are reported separately'}
            if name == 'exporter-saturation' else compare('diagnostics-release-disabled-sqlite-http1', 'diagnostics-' + name + '-sqlite-http1'))
    routes = next((case['report']['routes'] for case in reports if 'report' in case), {})
    engineering = rendering.inventory(routes, state['inputs']['package_probe'], state['inputs']['instrumented'])
    result = {'schema_version': 1, 'task': 'B23', 'platform': 'linux-x64',
        'source_cohorts': {'path': str(output / 'cohorts.json'), 'sha256': digest(output / 'cohorts.json')},
        'state': state['state'], 'counts': state.get('counts'), 'selection': state.get('selection'),
        'search': state['search'], 'cases': reports, 'diagnostic_comparisons': diagnostic,
        'di_comparison': compare('minimal-list-sqlite-http1', 'advanced-di-list-sqlite-http1'),
        'engineering_cost': engineering,
        'claim': 'Separate immutable per-case calibration/confirmation. Invalid calibration and confirmation remain adverse; no retries, band reuse or original high-rate pass is inferred.',
        'original_adverse_evidence': state['plan']['original_adverse_evidence'],
        'acceptance_dependency': 'B22 current Linux correctness/resource acceptance, with user-directed24h/72h nonexecution explicitly retained; Windows remains outside scope.'}
    save(output / 'report.json', result)
    lines = ['# B23 Linux operating-point and full-workload report', '', result['claim'], '',
        '## Operating-point selection', '',
        '| Family | Selected offered rate /s | Search valid |', '| --- | ---: | --- |']
    for selected in state.get('selection', []):
        lines.append(f"| {selected['family']} | {selected['rate']} | {selected['search_validated']} |")
    lines += ['', 'The original high-rate adverse rows remain separately preserved. Selection does not estimate maximum sustainable capacity. '
        'Payloads, database durability, route semantics and generator bounds are unchanged. '
        'Warm-cycle-low retains one client slot and a20/s cap; high retains16 slots. '
        'Cold create,8MiB streaming, deliberate overload and all diagnostic modes retain their original profiles.', '',
        '## Independent process cohorts', '',
        '| Case / route | Recorded phase | Valid processes | p50 / p95 / p99 / p99.9 (ms) | CPU/op (ms) | RSS after (MiB) |',
        '| --- | --- | --- | --- | ---: | ---: |']
    for case in reports:
        for group in case.get('report', {}).get('groups', []):
            percentiles = group['histogram']['percentiles_from_exact_samples_ns'] if group['histogram'] else {}
            latency = ' / '.join(rendering.number(percentiles.get(key),1e6) for key in ('p50','p95','p99','p99.9'))
            lines.append(f"| {case['case']} / {group['route']} | {case['phase']} | {group['valid_repetitions']}/{group['declared_repetitions']} | {latency} | "
                f"{rendering.number(group['application_cpu_seconds_per_offered_operation'],.001)} | {rendering.number(group['mean_rss_after_bytes'],1048576)} |")
    lines += ['', 'Percentiles describe the pooled raw samples in the stated phase; incomplete cohorts cannot establish a performance conclusion. '
        'p99.9 is unavailable below1000 samples. Confidence intervals and measured noise bands compare independent process means, not pooled request counts.', '',
        '## Route comparisons and adverse cases', '']
    for case in reports:
        value = case.get('report', {})
        lines.append('- ' + case['case'] + ': ' + case['phase'] + '; ' + str(value.get('state', case['state'])) + '.')
        if value.get('comparisons'):
            lines.append('  ' + json.dumps(value['comparisons'], sort_keys=True))
    lines += ['', '## Diagnostic and DI comparisons', '',
        'Each comparison requires both independent complete confirmation cohorts and the larger of their pre-frozen measured noise bands. '
        'All comparisons reuse the unmodified A21 independent-process bootstrap.', '']
    for name, comparison in diagnostic.items():
        lines.append('- ' + name + ': ' + json.dumps(comparison, sort_keys=True))
    lines.append('- Minimal versus request DI: ' + json.dumps(result['di_comparison'], sort_keys=True))
    lines += ['', '## Deployment and developer cost', '',
        '| Route | Runtime deployment (MiB) | Executable (MiB) | Native modules (MiB) |', '| --- | ---: | ---: | ---: |']
    for name, route in engineering['routes'].items():
        roles = route['bytes_by_role']
        lines.append(f"| {name} | {rendering.number(route['runtime_deployment_bytes'],1048576)} | "
            f"{rendering.number(roles.get('runtime-executable',0),1048576)} | {rendering.number(roles.get('native-module',0),1048576)} |")
    lines += ['', engineering['optional_protocol_closure'], '',
        'The JSON records exact package/source/lock/executable identities, physical source lines by role and previously recorded build/edit timings. '
        'Ordinary model/DTO/Handler edits reuse byte-identical native modules. Build observations reuse existing receipts and available caches; '
        'no clean-machine developer-time comparison is invented. Source moved into packages/generated/native layers remains explicit.', '']
    lines += ['', 'Every raw process record, parsed failed HTTP response, supervised cleanup receipt and independent durable oracle remains linked from the JSON. '
        'Process termination is not a retirement proof. Unavailable native allocator/FFI/copy counters remain null; RSS is not native allocation. '
        'All supported original cases are attempted without dropping unfavorable profiles; unsupported comparator modes remain explicit. '
        'This report does not replace B22 correctness/resource acceptance or B24 pristine package/release acceptance.', '']
    (output / 'report.md').write_text('\n'.join(lines))
    print(json.dumps({'report': str(output / 'report.json'), 'cases': len(reports)}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stage', choices=['prepare', 'search', 'matrix', 'report'])
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--plan', type=Path, default=HERE / 'operating-point-plan-v1.json')
    parser.add_argument('--package-probe', type=Path)
    parser.add_argument('--comparators', type=Path)
    parser.add_argument('--instrumented', type=Path)
    parser.add_argument('--postgres-bin', type=Path)
    parser.add_argument('--isolation-note')
    args = parser.parse_args()
    if args.stage == 'prepare':
        if any(getattr(args, key) is None for key in ('package_probe','comparators','instrumented','postgres_bin','isolation_note')):
            parser.error('prepare requires the four exact artifact locations and an actual isolation note')
        prepare(args)
    else:
        output = args.output.resolve()
        state = load(output / 'cohorts.json')
        check_files(state['frozen_files'])
        if state.get('execution_blocker') and args.stage != 'report':
            raise ValueError('Further invocations are blocked; inspect the persisted cleanup/stop evidence')
        {'search': search, 'matrix': matrix, 'report': write_report}[args.stage](output, state)
        if state.get('execution_blocker'):
            return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
