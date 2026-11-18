#!/usr/bin/env python3
"""Run a frozen B22 Linux stage with independent descriptor-role witnesses.

The existing soak controller, application and trace observer are unchanged.
This external supervisor adds prospective FD evidence; its receipt is required
alongside the ordinary stage receipt for the supplemented profile to pass.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
from urllib.parse import urlsplit

from harness import HERE, require, sha, write_json
from soak import Journal, Tail


def process_identity(pid):
    fields = (Path('/proc') / str(pid) / 'stat').read_text().rsplit(')', 1)[1].split()
    return {'pid': pid, 'parentPid': int(fields[1]), 'startTicks': int(fields[19])}


def snapshot(target, output, http_port, postgres_port, phase):
    require(process_identity(target['pid']) == target, 'Observed application PID identity changed')
    started = time.monotonic_ns()
    process = Path('/proc') / str(target['pid'])
    sockets = {}
    for table in ('tcp', 'tcp6'):
        for line in (process / 'net' / table).read_text().splitlines()[1:]:
            fields = line.split()
            local, remote, state, inode = fields[1], fields[2], fields[3], fields[9]
            local_port, remote_port = int(local.rsplit(':', 1)[1], 16), int(remote.rsplit(':', 1)[1], 16)
            role = ('postgresql' if remote_port == postgres_port else
                    'listener' if local_port == http_port and state == '0A' else
                    'acceptedHttp' if local_port == http_port else 'unclassified')
            sockets[inode] = {'role': role, 'table': table, 'localPort': local_port,
                             'remotePort': remote_port, 'stateHex': state}
    mapping_finished = time.monotonic_ns()
    entries = list((process / 'fd').iterdir())
    inventory, vanished, roles, categories = [], 0, {}, {}
    open_parts = 0
    for entry in entries:
        try:
            owner = os.readlink(entry)
        except FileNotFoundError:
            vanished += 1
            continue
        item = {'descriptor': int(entry.name)}
        if owner.startswith('socket:'):
            category = 'socket'
            endpoint = sockets.get(owner[8:-1])
            role = endpoint['role'] if endpoint else 'unmatched'
            roles[role] = roles.get(role, 0) + 1
            item.update(owner=owner, endpoint=endpoint)
        elif owner.startswith('pipe:') or owner.startswith('anon_inode:'):
            category = 'pipe' if owner.startswith('pipe:') else owner
            item['owner'] = owner
        elif owner == '/dev/null':
            category = 'nullDevice'
            item['owner'] = owner
        else:
            try:
                relative = str(Path(owner).relative_to(output))
                category = 'fixtureFile'
                item['owner'] = relative
                if relative.startswith('blobs/staging/') and '.part' in Path(relative).name:
                    open_parts += 1
            except ValueError:
                category = 'otherFile'
                # Only task-owned paths are retained; unrelated path contents
                # are unnecessary for descriptor-role acceptance.
                item['owner'] = Path(owner).name
        item['category'] = category
        categories[category] = categories.get(category, 0) + 1
        inventory.append(item)
    require(process_identity(target['pid']) == target, 'Application identity changed during FD snapshot')
    return {'phase': phase, 'target': target, 'startedMonotonicNs': started,
            'mappingFinishedMonotonicNs': mapping_finished, 'endedMonotonicNs': time.monotonic_ns(),
            'enumerated': len(entries), 'resolved': len(inventory), 'vanishedDuringRead': vanished,
            'socketRoles': roles, 'categories': categories, 'openStagingParts': open_parts,
            'inventory': inventory}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--artifact-directory', type=Path, required=True)
    parser.add_argument('--trace-reader', type=Path, required=True)
    parser.add_argument('--provider', choices=('sqlite', 'postgresql'), default='postgresql')
    parser.add_argument('--postgres-bin', type=Path)
    parser.add_argument('--certificate', type=Path, required=True)
    parser.add_argument('--private-key', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    profile = json.loads(args.profile.read_text())
    monitor_sha = sha(Path(__file__))
    require(profile['status'] == 'frozen' and profile['identity']['provider'] == args.provider,
            'Descriptor supplement requires its own frozen provider profile')
    supplement = profile['fdThresholdSupplement']
    require(supplement['descriptorMonitorSha256'] == monitor_sha,
            'External descriptor monitor differs from its reviewed frozen input')
    output = args.output.resolve()
    monitor_output = output.with_name(output.name + '-descriptors')
    monitor_output.mkdir(parents=True, exist_ok=False)
    command = [sys.executable, str(HERE / 'soak.py'), 'run', '--stage', 'steady-30m',
               '--provider', args.provider, '--mode', 'aot', '--dotnet', args.dotnet]
    for name in ('profile', 'output', 'artifact_directory', 'trace_reader', 'postgres_bin',
                 'certificate', 'private_key'):
        if getattr(args, name) is not None:
            command += ['--' + name.replace('_', '-'), str(getattr(args, name).resolve())]
    receipt = {'schemaVersion': 1, 'task': 'B22', 'status': 'running',
               'profileSha256': sha(args.profile), 'monitorSha256': monitor_sha,
               'command': command, 'sampleSeconds': 1.1, 'startedUnixNs': time.time_ns(),
               'fullB22Acceptance': False, 'multiDayAcceptance': False,
               'interpretation': 'Prospective practical FD band plus classified live idle witness; not a universal process-FD bound.'}
    write_json(monitor_output / 'request.json', receipt)
    samples = Journal(monitor_output / 'counts.jsonl')
    peaks = Journal(monitor_output / 'peak-inventories.jsonl')
    last_idle = target = None
    maximum = 0
    child = None
    previous_handlers = {}
    try:
        with (monitor_output / 'controller.log').open('wb') as log:
            child = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
            receipt['controllerPid'] = child.pid
            def stop(signum, _frame):
                if child.poll() is None:
                    child.send_signal(signum)
            previous_handlers = {sig: signal.signal(sig, stop) for sig in (signal.SIGTERM, signal.SIGINT)}
            tail = Tail(output / 'resources.jsonl')
            while child.poll() is None:
                stop_path = monitor_output / 'stop-request.json'
                if stop_path.exists() and 'stopRequested' not in receipt:
                    with stop_path.open('rb') as stream:
                        payload = stream.read(4097)
                    require(len(payload) <= 4096, 'Stop request exceeds its finite input bound')
                    request = json.loads(payload)
                    require(request.get('action') == 'stop' and
                            isinstance(request.get('reason'), str) and
                            0 < len(request['reason']) <= 512,
                            'Stop request must contain a bounded explicit reason')
                    receipt['stopRequested'] = {'reason': request['reason'],
                                                'observedUnixNs': time.time_ns(),
                                                'requestSha256': sha(stop_path)}
                    write_json(monitor_output / 'stop-acknowledged.json', receipt['stopRequested'])
                    # The child object belongs to this supervisor's namespace.
                    # Tool-level Ctrl-C may kill that namespace before cleanup.
                    child.send_signal(signal.SIGTERM)
                progress_path = output / 'progress.json'
                if progress_path.exists():
                    progress = json.loads(progress_path.read_text())
                    pid = progress.get('serviceProcessId')
                    if pid is not None:
                        try:
                            actual = process_identity(pid)
                            if target is None:
                                require(actual['parentPid'] == child.pid, 'Target is not the launched controller child')
                                target = actual
                                config = json.loads((output / 'service/config.private.json').read_text())
                                postgres_port = urlsplit(config['database']).port
                                ready = next(line[6:] for line in (output / 'service/service.log').read_text().splitlines()
                                             if line.startswith('READY '))
                                http_port = urlsplit(ready).port
                                receipt.update(target=target, httpPort=http_port, postgresPort=postgres_port)
                                write_json(monitor_output / 'request.json', receipt)
                            require(actual == target, 'Application PID was reused or reparented')
                            tail.read()
                            phase = tail.latest['phase'] if tail.latest else 'startup'
                            row = snapshot(target, output, http_port, postgres_port, phase)
                            samples.write({key: value for key, value in row.items() if key != 'inventory'})
                            if row['enumerated'] > maximum:
                                maximum = row['enumerated']
                                peaks.write(row)
                            require(row['enumerated'] <= profile['peakCaps']['fileDescriptors'],
                                    'External inventory exceeded the frozen peak descriptor cap')
                            if phase == 'idle':
                                last_idle = row
                        except FileNotFoundError:
                            # Closure races remain absent observations, never fabricated zeroes.
                            receipt['processReadRaces'] = receipt.get('processReadRaces', 0) + 1
                time.sleep(1.1)
            exit_code = child.wait()
        stage = json.loads((output / 'receipt.json').read_text())
        receipt.update(controllerExitCode=exit_code, soakReceiptSha256=sha(output / 'receipt.json'),
                       soakStatus=stage['status'], peakEnumeratedDescriptors=maximum)
        require(exit_code == 0 and stage['status'] == 'stage-passed', 'Original frozen soak stage did not pass')
        require(last_idle is not None, 'No live application idle descriptor observation was obtained')
        require(last_idle['enumerated'] <= profile['idleCaps']['fileDescriptors'],
                'External inventory exceeded the frozen idle descriptor cap')
        for category, count in last_idle['categories'].items():
            if category != 'socket':
                require(category in supplement['idleDescriptorClassCaps'] and
                        count <= supplement['idleDescriptorClassCaps'][category],
                        'Idle retained an unexplained non-socket descriptor class or count')
        file_counts = {}
        for item in last_idle['inventory']:
            if item['category'] == 'fixtureFile':
                owner = item['owner']
                file_counts[owner] = file_counts.get(owner, 0) + 1
        require(all(owner in supplement['idlePersistentFixtureOwnerCaps'] and
                    count <= supplement['idlePersistentFixtureOwnerCaps'][owner]
                    for owner, count in file_counts.items()),
                'Idle retained a blob payload, staging, metadata, or unexplained fixture file')
        roles = last_idle['socketRoles']
        require(last_idle['vanishedDuringRead'] == 0 and roles.get('unmatched', 0) == 0 and
                roles.get('unclassified', 0) == 0, 'Final idle descriptor ownership is ambiguous')
        require(roles.get('acceptedHttp', 0) == 0 and last_idle['openStagingParts'] == 0,
                'Live idle retained an accepted HTTP socket or staged upload file')
        require(roles.get('listener', 0) == 1,
                'Live idle listener inventory is outside the declared band')
        if args.provider == 'postgresql':
            require(0 < roles.get('postgresql', 0) <= supplement['configuredPostgresPoolMaximum'],
                    'Live idle PostgreSQL socket inventory is outside the declared band')
        else:
            require(roles.get('postgresql', 0) == 0,
                    'SQLite profile unexpectedly retained a PostgreSQL socket')
        require(0 <= stage['stageCompletedBeforeClosureNs'] - last_idle['endedMonotonicNs'] <= 3_300_000_000,
                'Descriptor idle witness is stale or follows process closure')
        require(sha(Path(__file__)) == monitor_sha and sha(args.profile) == receipt['profileSha256'],
                'Frozen monitor/profile inputs changed during execution')
        write_json(monitor_output / 'final-live-idle.json', last_idle)
        receipt.update(status='supplemented-stage-passed', finalLiveIdleSha256=sha(monitor_output / 'final-live-idle.json'))
    except BaseException as error:
        receipt.update(status='interrupted' if 'stopRequested' in receipt else 'failed',
                       error={'type': type(error).__name__, 'message': str(error)})
        if child is not None and child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=30)
            except subprocess.TimeoutExpired:
                # Do not claim retirement when forced supervisor termination is needed.
                child.kill()
                child.wait(timeout=5)
                receipt['forcedControllerKill'] = True
                receipt['descendantCleanup'] = 'unknown; root must resolve target/fixture cleanup before reusing this load slot'
    finally:
        samples.close()
        peaks.close()
        for sig, handler in previous_handlers.items():
            signal.signal(sig, handler)
        receipt.update(endedUnixNs=time.time_ns(), numericSamplesSha256=sha(monitor_output / 'counts.jsonl'),
                       peakInventoriesSha256=sha(monitor_output / 'peak-inventories.jsonl'))
        write_json(monitor_output / 'receipt.json', receipt)
    print(json.dumps({'status': receipt['status'], 'receipt': str(monitor_output / 'receipt.json')}), flush=True)
    return 0 if receipt['status'] == 'supplemented-stage-passed' else 1


if __name__ == '__main__':
    sys.exit(main())
