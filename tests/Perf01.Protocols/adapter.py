#!/usr/bin/env python3
"""A21 wire adapter: a real package AOT server and an independent client process."""
import argparse
import asyncio
import importlib.util
import json
import os
from pathlib import Path
import signal
import sys
import time


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


def save(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


async def main(args):
    manifest = json.loads(args.manifest.read_text())
    a21 = load_module('perf01_a21', manifest['a21'])
    sys.path.insert(0, str(Path(manifest['domain_common']).parent))
    common = load_module('perf01_domain_common', manifest['domain_common'])
    out = Path(manifest['run_directory']); out.mkdir(parents=True, exist_ok=False)
    for path, digest in manifest['artifacts'].items():
        if a21.sha256(path) != digest: raise ValueError('Frozen artifact mismatch: ' + path)
    server, client = None, None
    readers, resources = [], []
    stop_sampling = asyncio.Event()
    receipt = {'status': 'incomplete', 'manifest_sha256': a21.sha256(args.manifest), 'loaded_native_sha256': {}, 'errors': []}
    env = dict(os.environ, DOTNET_PROCESSOR_COUNT=str(manifest['process_cpu_count']), DOTNET_CLI_TELEMETRY_OPTOUT='1')
    async def log_stream(stream, path):
        with path.open('wb') as log:
            while chunk := await stream.read(8192): log.write(chunk); log.flush()
    async def send_server(phase):
        if server and server.returncode is None:
            server.stdin.write((phase + '\n').encode()); await server.stdin.drain()
    async def sample():
        with (out / 'process-resources.jsonl').open('w') as log:
            while not stop_sampling.is_set():
                row = {'monotonic_ns': time.monotonic_ns(), 'utc_ns': time.time_ns(),
                       'server': common.process_sample(a21, server.pid),
                       'client': common.process_sample(a21, client.pid) if client else None,
                       'adapter': common.process_sample(a21, os.getpid()),
                       'controller': common.process_sample(a21, os.getppid()),
                       'load_average': os.getloadavg()}
                resources.append(row); log.write(json.dumps(row, separators=(',', ':')) + '\n'); log.flush()
                try: await asyncio.wait_for(stop_sampling.wait(), 1)
                except asyncio.TimeoutError: pass
    async def terminate(process):
        if process and process.returncode is None:
            process.terminate()
            try: await asyncio.wait_for(process.wait(), 5)
            except asyncio.TimeoutError:
                process.kill(); await process.wait()
    sampling = None
    try:
        server_command = [manifest['server'], '--config', manifest['server_config'], '--output', str(out / 'server')]
        server = await asyncio.create_subprocess_exec(*server_command, stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
                                                     stderr=asyncio.subprocess.PIPE, env=env)
        save(out / 'processes.json', {'server': server.pid, 'adapter': os.getpid()})
        readers.append(asyncio.create_task(log_stream(server.stderr, out / 'server-stderr.log')))
        line = await asyncio.wait_for(server.stdout.readline(), 8)
        if not line.startswith(b'READY '): raise ValueError('AOT server did not emit READY: ' + line[:300].decode(errors='replace'))
        ready = json.loads(line[6:]); save(out / 'ready.json', ready)
        if ready['dynamic_code_supported'] is not False: raise ValueError('Server is not Native AOT or native Rust')
        if len(ready['addresses']) != 2: raise ValueError('Expected separate TLS HTTP/1 and HTTP/2 listeners')
        expected = set(manifest['native_sha256'].values())
        actual = set(ready['loaded_native_sha256'].values())
        if manifest['route'] == 'nativeweave-aot-rust' and (not expected or expected != actual):
            raise ValueError('Actually loaded native modules differ from build manifest')
        if manifest['route'] == 'direct-rust' and actual: raise ValueError('Direct Rust server unexpectedly loaded a NativeWeave module')
        receipt['loaded_native_sha256'] = ready['loaded_native_sha256']
        client_config = dict(manifest['client_config'], http1=ready['addresses'][0], http2=ready['addresses'][1])
        save(out / 'client-config.json', client_config)
        client = await asyncio.create_subprocess_exec(manifest['client'], '--config', str(out / 'client-config.json'), '--output', str(out / 'client'),
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, env=env)
        save(out / 'processes.json', {'server': server.pid, 'client': client.pid, 'adapter': os.getpid()})
        readers.append(asyncio.create_task(log_stream(client.stderr, out / 'client-stderr.log')))
        client_hello = json.loads(await asyncio.wait_for(client.stdout.readline(), 8))
        if client_hello.get('kind') != 'hello' or client_hello.get('dynamic_code_supported') is not False:
            raise ValueError('Independent client is not Native AOT or did not emit hello')
        receipt.update(server_pid=server.pid, client_pid=client.pid, client_hello=client_hello)
        sampling = asyncio.create_task(sample())
        print(json.dumps({'kind': 'hello', 'route': manifest['route'], 'dynamic_code_supported': False,
                          'server_pid': server.pid, 'client_pid': client.pid}), flush=True)
        await send_server('warmup')
        async def forward_output():
            while line := await client.stdout.readline():
                value = json.loads(line)
                print(json.dumps(value, separators=(',', ':')), flush=True)
        forwarding = asyncio.create_task(forward_output())
        reader = asyncio.StreamReader(); protocol = asyncio.StreamReaderProtocol(reader)
        await asyncio.get_running_loop().connect_read_pipe(lambda: protocol, sys.stdin)
        first = True; recovery = False
        while line := await reader.readline():
            identifier = int(line.split(b'\t', 1)[0])
            if first and identifier < manifest['requests']:
                first = False; receipt['measurement_dispatch_start_utc_ns'] = time.time_ns()
                receipt['measurement_resources_before'] = common.process_sample(a21, server.pid)
                await send_server('measurement')
            if not first and not recovery and identifier >= manifest['requests']:
                recovery = True
                receipt['measurement_resources_after'] = common.process_sample(a21, server.pid)
                receipt['measurement_dispatch_end_utc_ns'] = time.time_ns()
                receipt['recovery_start_utc_ns'] = time.time_ns()
                await send_server('recovery')
            client.stdin.write(line); await client.stdin.drain()
        client.stdin.close()
        await asyncio.wait_for(client.wait(), manifest['client_timeout_seconds'] + 10)
        await forwarding
        receipt['recovery_end_utc_ns'] = time.time_ns()
        receipt['recovery_resources_after'] = common.process_sample(a21, server.pid)
        if client.returncode != 0: raise ValueError('Independent client exited with ' + str(client.returncode))
        await send_server('idle')
        receipt['idle_start_utc_ns'] = time.time_ns()
        await asyncio.sleep(manifest['idle_seconds'])
        receipt['idle_end_utc_ns'] = time.time_ns()
        receipt['after_idle_resources'] = common.process_sample(a21, server.pid)
        await send_server('stop'); server.stdin.close()
        tail = await asyncio.wait_for(server.stdout.read(), 40)
        (out / 'server-stdout-tail.log').write_bytes(tail)
        await asyncio.wait_for(server.wait(), 5)
        if server.returncode != 0: raise ValueError('Server exited with ' + str(server.returncode))
        closed = json.loads((out / 'server/closed.json').read_text()); receipt['closed'] = closed
        if any(closed.get(key) != value for key, value in {'status': 'Completed', 'pending_requests': 0, 'retained_managed_bytes': 0, 'retained_native_bytes': 0, 'state': 'Disposed'}.items()):
            raise ValueError('Native owner retirement did not complete')
        receipt['status'] = 'passed'
    except BaseException as error:
        receipt['status'] = 'failed'; receipt['errors'].append(type(error).__name__ + ': ' + str(error)[:1000]); raise
    finally:
        await terminate(client); await terminate(server)
        stop_sampling.set()
        if sampling: await sampling
        await asyncio.gather(*readers, return_exceptions=True)
        receipt['resource_sample_count'] = len(resources)
        receipt['limitations'] = 'Socket descriptors are not unique HTTP/2 streams; CPU/procfs/GC/gauges are separate observations. Process exit alone is not retirement proof.'
        save(out / 'receipt.json', receipt)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    asyncio.run(main(parser.parse_args()))
