"""Observed Linux CPU ownership for the optional codec generator diagnostic."""
import argparse
import os
from pathlib import Path
import time


def cpu_pair(value):
    parts = value.split(',')
    if len(parts) != 2 or any(not part.isascii() or not part.isdecimal() for part in parts):
        raise argparse.ArgumentTypeError('Use exactly two distinct CPU numbers, for example 0,1.')
    try:
        cpus = sorted(int(part) for part in parts)
    except ValueError as error:
        raise argparse.ArgumentTypeError('CPU numbers must be finite nonnegative integers.') from error
    if cpus[0] == cpus[1] or cpus[-1] >= 65536:
        raise argparse.ArgumentTypeError('Use two distinct CPU numbers in 0..65535.')
    return cpus


def observe(pid):
    process = Path('/proc') / str(pid)
    fields = (process / 'stat').read_text().rsplit(')', 1)[1].split()
    threads = {}
    for entry in (process / 'task').iterdir():
        try:
            threads[entry.name] = sorted(os.sched_getaffinity(int(entry.name)))
        except ProcessLookupError:
            pass  # A thread can retire between the directory read and syscall.
    return {'pid': pid, 'start_ticks': int(fields[19]), 'main_cpus': sorted(os.sched_getaffinity(pid)),
            'threads': threads, 'monotonic_ns': time.monotonic_ns()}


def require(snapshot, expected):
    if snapshot['main_cpus'] != sorted(expected) or not snapshot['threads']:
        raise ValueError('Process main CPU mask differs: ' + str(snapshot['pid']))
    if any(not cpus or not set(cpus) <= set(expected) for cpus in snapshot['threads'].values()):
        raise ValueError('Process thread escaped the declared CPU mask: ' + str(snapshot['pid']))


def pin_current_threads(cpus):
    # Child creation may already have started an asyncio waitpid thread. Move
    # the main thread first so its future threads inherit the generator mask,
    # then every current TID, and verify the observed process-wide restriction.
    os.sched_setaffinity(0, cpus)
    for entry in (Path('/proc') / str(os.getpid()) / 'task').iterdir():
        try:
            os.sched_setaffinity(int(entry.name), cpus)
        except ProcessLookupError:
            pass
    snapshot = observe(os.getpid())
    require(snapshot, cpus)
    return snapshot
