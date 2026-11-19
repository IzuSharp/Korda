"""Linux CPU-mask observations for the optional Perf-01 generator split."""
import argparse
import os
from pathlib import Path
import time


def cpu_list(value):
    try:
        result = set()
        for part in value.split(','):
            bounds = [int(item) for item in part.split('-')]
            if len(bounds) == 1 and 0 <= bounds[0] < 65536: result.add(bounds[0])
            elif len(bounds) == 2 and 0 <= bounds[0] <= bounds[1] < 65536: result.update(range(bounds[0], bounds[1] + 1))
            else: raise ValueError('Invalid CPU range')
        if not result: raise ValueError('Empty CPU list')
        return sorted(result)
    except ValueError as error:
        raise argparse.ArgumentTypeError('Use a CPU list such as 0,1') from error


def observe(pid):
    process = Path('/proc') / str(pid)
    fields = (process / 'stat').read_text().rsplit(')', 1)[1].split()
    threads = {}
    for entry in (process / 'task').iterdir():
        try: threads[entry.name] = sorted(os.sched_getaffinity(int(entry.name)))
        except ProcessLookupError: pass
    return {'pid': pid, 'parent_pid': int(fields[1]), 'start_ticks': int(fields[19]),
            'main_cpus': sorted(os.sched_getaffinity(pid)), 'threads': threads, 'monotonic_ns': time.monotonic_ns()}


def require(snapshot, expected):
    if snapshot['main_cpus'] != sorted(expected) or not snapshot['threads']:
        raise ValueError('Process main CPU mask differs: ' + str(snapshot['pid']))
    if any(not mask or not set(mask) <= set(expected) for mask in snapshot['threads'].values()):
        raise ValueError('A process thread runs outside the declared mask: ' + str(snapshot['pid']))


def pin_current_threads(cpus):
    # asyncio's child watcher can already have a waitpid thread after spawning
    # the service/adapter. Move every current Python thread, not only the main.
    os.sched_setaffinity(0, cpus)
    for entry in (Path('/proc') / str(os.getpid()) / 'task').iterdir():
        try: os.sched_setaffinity(int(entry.name), cpus)
        except ProcessLookupError: pass
    snapshot = observe(os.getpid()); require(snapshot, cpus)
    return snapshot
