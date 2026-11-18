"""Bounded Linux process-tree supervision outside A21's measurement process.

PID identities always include their procfs start time. A private per-cell
registration chain retains ownership when an adapter exits before its child.
No PID from another execution namespace is ever used for signalling.
"""
from __future__ import annotations

import asyncio
import json
import math
import os
from pathlib import Path
import secrets
import signal
import sys

from common import digest, load_a21, save


def identity(pid):
    try:
        fields = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
        return {'pid': pid, 'ppid': int(fields[1]), 'start_ticks': int(fields[19]), 'state': fields[0]}
    except (OSError, ValueError, IndexError):
        return None


def register_process(role, pid, evidence_directory=None):
    """Called by trusted B23 adapter code immediately after owned process creation."""
    registry, token = os.environ.get('NW_B23_PROCESS_REGISTRY'), os.environ.get('NW_B23_CELL_TOKEN')
    if not registry or not token:
        return
    process = identity(pid)
    if process is None:
        return
    owner = identity(process['ppid'])
    if owner is None:
        return
    value = dict(process, role=role, token=token, owner_start_ticks=owner['start_ticks'],
                 evidence_directory=str(evidence_directory) if evidence_directory else None)
    descriptor = os.open(registry, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    with os.fdopen(descriptor, 'w') as stream:
        stream.write(json.dumps(value, separators=(',', ':')) + '\n')


class OwnedTree:
    """Track only the spawned root and its observed/registered descendants."""
    def __init__(self, root, registry, token):
        initial = identity(root)
        if initial is None:
            raise RuntimeError('Worker disappeared before its identity was observed')
        self.root, self.registry, self.token = root, Path(registry), token
        self.known = {root: dict(initial, role='cell-worker')}
        self.signals, self.registry_errors = [], []

    def refresh(self):
        if self.registry.exists():
            with self.registry.open('rb') as stream:
                data = stream.read(1_048_577)
            if len(data) > 1_048_576:
                if 'Registry exceeded its finite bound' not in self.registry_errors:
                    self.registry_errors.append('Registry exceeded its finite bound')
                data = b''
            for line in data.splitlines(keepends=True):
                if not line.endswith(b'\n'):
                    continue
                try:
                    record = json.loads(line)
                    owner = self.known.get(record['ppid'])
                    if record['token'] != self.token or not owner or owner['start_ticks'] != record['owner_start_ticks']:
                        continue
                    # The trusted adapter may have exited before this poll. Its
                    # registered identity still establishes the child's owner
                    # chain; signal eligibility is checked against live procfs.
                    self.known[record['pid']] = record
                except (ValueError, KeyError, TypeError) as error:
                    if len(self.registry_errors) < 16:
                        self.registry_errors.append(type(error).__name__)
        # Covers short helper processes such as psql in this same namespace.
        current = {int(path.name): identity(int(path.name)) for path in Path('/proc').iterdir() if path.name.isdigit()}
        changed = True
        while changed:
            changed = False
            for pid, record in current.items():
                if record is None or pid in self.known:
                    continue
                owner = self.known.get(record['ppid'])
                live_owner = current.get(record['ppid'])
                if owner and live_owner and live_owner['start_ticks'] == owner['start_ticks']:
                    self.known[pid] = dict(record, role='observed-descendant')
                    changed = True

    def live(self):
        result = []
        for pid, record in self.known.items():
            current = identity(pid)
            if current and current['start_ticks'] == record['start_ticks'] and current['state'] != 'Z':
                result.append(current)
        return result

    def send(self, number):
        # Leaves first, including separately sessioned application processes.
        def depth(record):
            count, parent, seen = 0, record['ppid'], set()
            while parent in self.known and parent not in seen:
                seen.add(parent); count += 1; parent = self.known[parent]['ppid']
            return count
        for process in sorted(self.live(), key=depth, reverse=True):
            if any(record['pid'] == process['pid'] and record['start_ticks'] == process['start_ticks']
                   and record['signal'] == number for record in self.signals):
                continue
            descriptor = None
            try:
                descriptor = os.pidfd_open(process['pid'])
                current = identity(process['pid'])
                if current is None or current['start_ticks'] != process['start_ticks']:
                    continue
                signal.pidfd_send_signal(descriptor, number)
                self.signals.append({'pid': process['pid'], 'start_ticks': process['start_ticks'], 'signal': number})
            except ProcessLookupError:
                pass
            finally:
                if descriptor is not None:
                    os.close(descriptor)

    async def stop(self, grace_seconds, a21):
        self.refresh()
        self.send(signal.SIGTERM)
        deadline = a21.time.monotonic_ns() + int(grace_seconds * 1e9)
        while self.live() and a21.time.monotonic_ns() < deadline:
            await asyncio.sleep(.05)
            self.refresh()
        if self.live():
            self.send(signal.SIGKILL)
            deadline = a21.time.monotonic_ns() + 2_000_000_000
            while self.live() and a21.time.monotonic_ns() < deadline:
                await asyncio.sleep(.05)
                self.refresh()
                self.send(signal.SIGKILL)
        return self.live()


def stop_requested(path):
    path = Path(path)
    if not path.exists():
        return False
    if path.stat().st_size > 8192:
        return True
    try:
        return json.loads(path.read_text()).get('stop') is True
    except (ValueError, AttributeError):
        return True


async def supervised_command(command, directory, timeout_seconds, grace_seconds, control_file, a21=None):
    """A bounded worker wait with explicit owner cleanup, even when A21 hangs."""
    if sys.platform != 'linux':
        raise RuntimeError('This selected supervisor requires Linux procfs; other platform acceptance is not implemented here')
    if not hasattr(os, 'pidfd_open') or not hasattr(signal, 'pidfd_send_signal'):
        raise RuntimeError('The selected Linux supervisor requires identity-bound pidfd signalling')
    if not math.isfinite(timeout_seconds) or not math.isfinite(grace_seconds) or timeout_seconds <= 0 or grace_seconds < 0:
        raise ValueError('Supervisor deadlines must be positive and finite')
    a21 = a21 or load_a21()
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=False)
    registry = directory / 'owned-processes.jsonl'
    token = secrets.token_hex(16)
    environment = dict(os.environ, NW_B23_PROCESS_REGISTRY=str(registry), NW_B23_CELL_TOKEN=token)
    started = a21.time.monotonic_ns()
    receipt = {'schema_version': 1, 'command': command, 'timeout_seconds': timeout_seconds,
        'termination_grace_seconds': grace_seconds, 'control_file': str(control_file),
        'status': 'running', 'pid_scope': 'Only identities registered/observed in this execution namespace',
        'clock': 'A21 monotonic clock; supervision boundaries are not request latency statistics'}
    save(directory / 'receipt.json', receipt)
    worker, tree, reason = None, None, None
    remaining = []
    try:
        with (directory / 'worker.log').open('w') as log:
            worker = await asyncio.create_subprocess_exec(*command, stdout=log, stderr=asyncio.subprocess.STDOUT,
                env=environment, start_new_session=True)
            tree = OwnedTree(worker.pid, registry, token)
            receipt['worker_pid'] = worker.pid
            save(directory / 'receipt.json', receipt)
            deadline = started + int(timeout_seconds * 1e9)
            while worker.returncode is None:
                tree.refresh()
                if tree.registry_errors:
                    reason = 'owned-process registry could not be validated'
                    break
                if stop_requested(control_file):
                    reason = 'operator stop control'
                    break
                if a21.time.monotonic_ns() >= deadline:
                    reason = 'external cell watchdog deadline'
                    break
                await asyncio.sleep(.1)
            tree.refresh()
            if reason is None and tree.live():
                reason = 'worker exited with a live owned descendant'
            if reason is not None:
                remaining = await tree.stop(grace_seconds, a21)
            try:
                await asyncio.wait_for(worker.wait(), 2)
            except asyncio.TimeoutError:
                reason = reason or 'worker did not reap after bounded termination'
            receipt['worker_exit_code'] = worker.returncode
            if worker.returncode != 0:
                reason = reason or 'cell worker exit: ' + str(worker.returncode)
    except BaseException as error:
        reason = reason or type(error).__name__ + ': ' + str(error)[:512]
        if tree is not None:
            try:
                remaining = await tree.stop(grace_seconds, a21)
            except Exception as cleanup_error:
                receipt['cleanup_error'] = type(cleanup_error).__name__ + ': ' + str(cleanup_error)[:512]
        raise
    finally:
        if tree is not None:
            remaining = tree.live()
        receipt.update(status='failed' if reason or remaining else 'completed', failure=reason,
            elapsed_ns=a21.time.monotonic_ns() - started, live_owned_processes_after_cleanup=remaining,
            owned_processes=list(tree.known.values()) if tree else [], signals=tree.signals if tree else [],
            registry_errors=tree.registry_errors if tree else [],
            application_retirement='Normal application receipt is checked separately; forced process termination never proves native retirement')
        if (directory / 'worker.log').exists():
            receipt['worker_log_sha256'] = digest(directory / 'worker.log')
        if registry.exists():
            receipt['registry_sha256'] = digest(registry)
        save(directory / 'receipt.json', receipt)
    return receipt
