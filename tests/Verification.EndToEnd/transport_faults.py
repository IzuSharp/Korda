#!/usr/bin/env python3
"""Actual B21 slow-client and active-upload shutdown cases for B22 Linux.

HTTP2 receive-window withholding is the slow-download barrier. No timeout or
client-side reset substitutes for positive delivery, durable bytes or shutdown.
"""
import argparse
import contextlib
import hashlib
import http.client
import json
import os
from pathlib import Path
import signal
import socket
import ssl
import time
import uuid
from urllib.parse import urlsplit

import h2.config
import h2.connection
import h2.events
import h2.settings

from faults import Faults, quote
from harness import Evidence, PostgresCluster, create, eventually, require, sha, write_json


def process_snapshot(process):
    """Read actual child procfs; these fields are not aggregate live roots."""
    fields = {}
    for line in Path(f'/proc/{process.pid}/status').read_text().splitlines():
        if line.startswith(('VmRSS:', 'VmHWM:', 'Threads:')):
            key, raw = line.split(':', 1); parts = raw.split()
            fields[key] = int(parts[0]) * (1024 if len(parts) > 1 and parts[1] == 'kB' else 1)
    fields['openFileDescriptors'] = len(list(Path(f'/proc/{process.pid}/fd').iterdir()))
    return fields


class Transports(Faults):
    def seed_item(self, service, token, key):
        status, body, _ = create(service, token, key, 'B22 actual slow-client fixture')
        self.evidence.check('fixture-create-committed', status == 201)
        return json.loads(body)['itemId']

    def slow_upload(self, path):
        config, token, sql = self.setup(path)
        data = bytes(range(256)) * 512
        with self.service(config, path / 'service') as service:
            item = self.seed_item(service, token, 'slow-upload-parent')
            transport = self.open_upload(service, token, item, len(data), data[:65536], 'slow-upload')
            try:
                staged = Path(config['blobDirectory']) / 'staging'
                eventually(lambda: any(p.stat().st_size > 0 for p in staged.glob('*.part')), 'actual partial upload bytes written')
                self.no_attachment(sql, item)
                self.evidence.event('slow-upload-positive-staging', clientBytesSent=65536,
                    declaredBytes=len(data), process=process_snapshot(service.process))
                # The body remains deliberately incomplete throughout this hold.
                deadline = time.monotonic() + 1.2
                while time.monotonic() < deadline:
                    require(service.process.poll() is None, 'Host exited during slow upload')
                    time.sleep(0.05)
                self.no_attachment(sql, item)
                for offset in range(65536, len(data), 16384):
                    transport.sendall(data[offset:offset + 16384]); time.sleep(0.05)
                response = http.client.HTTPResponse(transport); response.begin(); body = response.read()
                attached = json.loads(body)
                operation = str(uuid.UUID(attached['operationId']))
                self.evidence.check('slow-upload-completes-exact-response', response.status == 200
                    and operation == attached['operationId'] and attached['responseVersion'] == 1
                    and attached['itemId'] == item and attached['contentHash'] == hashlib.sha256(data).hexdigest()
                    and attached['contentLength'] == len(data) and attached['version'] == 2
                    and sql.replay(operation) == (1, 200, body),
                    status=response.status, responseBytes=len(body))
                response.close()
            finally:
                transport.close()
            status, downloaded, _ = service.request('GET', f'/v1/items/{item}/attachment', token=token)
            self.evidence.check('slow-upload-exact-durable-download', status == 200 and downloaded == data,
                bytes=len(downloaded), sha256=hashlib.sha256(downloaded).hexdigest())
            expected = hashlib.sha256(data).hexdigest()
            stored = sql.sql('SELECT content_hash,content_length FROM attachments WHERE item_id=' + quote(item))
            self.evidence.check('slow-upload-independent-reference', len(stored) == 1 and stored[0][0] == expected and int(stored[0][1]) == len(data))

    def slow_download(self, path):
        config, token, sql = self.setup(path)
        data = bytes(range(256)) * 2048
        with self.service(config, path / 'service', http2=True) as service:
            item = self.seed_item(service, token, 'slow-download-parent')
            status, _, _ = service.request('PUT', f'/v1/items/{item}/attachment', data,
                {'Content-Type': 'application/octet-stream', 'Idempotency-Key': 'slow-download-content', 'If-Match': '1'}, token)
            self.evidence.check('slow-download-content-committed', status == 200)
            address = urlsplit(service.url)
            context = ssl.create_default_context(cafile=str(self.tls[0])); context.set_alpn_protocols(['h2'])
            with socket.create_connection((address.hostname, address.port), timeout=15) as plain:
                with context.wrap_socket(plain, server_hostname=address.hostname) as transport:
                    self.evidence.check('slow-download-actual-http2', transport.selected_alpn_protocol() == 'h2')
                    protocol = h2.connection.H2Connection(config=h2.config.H2Configuration(client_side=True, header_encoding='utf-8'))
                    protocol.initiate_connection()
                    protocol.update_settings({h2.settings.SettingCodes.INITIAL_WINDOW_SIZE: 4096})
                    protocol.send_headers(1, [(':method', 'GET'), (':scheme', 'https'), (':authority', address.netloc),
                        (':path', f'/v1/items/{item}/attachment'), ('authorization', 'Bearer ' + token)], end_stream=True)
                    transport.sendall(protocol.data_to_send())
                    observed = bytearray(); response_headers = {}; held = []; ended = False; stalled = False
                    deadline = time.monotonic() + 20
                    while not ended:
                        require(time.monotonic() < deadline, 'Controlled slow download exceeded its finite bound')
                        incoming = transport.recv(65536); require(bool(incoming), 'HTTP2 transport ended without END_STREAM')
                        for event in protocol.receive_data(incoming):
                            if isinstance(event, h2.events.ResponseReceived): response_headers = dict(event.headers)
                            elif isinstance(event, h2.events.DataReceived):
                                require(event.stream_id == 1 and len(observed) + len(event.data) <= len(data), 'Unexpected/excess HTTP2 data')
                                observed.extend(event.data)
                                if not stalled: held.append(event)
                                else: protocol.acknowledge_received_data(event.flow_controlled_length, event.stream_id)
                            elif isinstance(event, h2.events.StreamEnded): ended = True
                            elif isinstance(event, (h2.events.StreamReset, h2.events.ConnectionTerminated)):
                                raise AssertionError('Slow download reset rather than complete delivery')
                        if held and not stalled:
                            self.evidence.check('slow-download-window-positive-barrier', response_headers.get(':status') == '200'
                                and 0 < len(observed) <= 4096 and not ended, deliveredBeforeHold=len(observed), initialWindow=4096)
                            start = time.monotonic(); samples = []; bytes_before = len(observed)
                            previous_timeout = transport.gettimeout()
                            # Continue draining and parsing the actual socket while
                            # withholding credit. Queued excess DATA must not be
                            # hidden until after WINDOW_UPDATE is finally sent.
                            while time.monotonic() - start < 2.2:
                                require(service.process.poll() is None, 'Host exited during receive-window hold')
                                transport.settimeout(min(0.2, max(0.001, 2.2 - (time.monotonic() - start))))
                                try:
                                    incoming = transport.recv(65536)
                                except socket.timeout:
                                    incoming = None
                                if incoming is not None:
                                    require(bool(incoming), 'HTTP2 transport ended during receive-window hold')
                                    for event in protocol.receive_data(incoming):
                                        if isinstance(event, h2.events.DataReceived):
                                            require(event.stream_id == 1, 'Unexpected HTTP2 stream during hold')
                                            observed.extend(event.data); held.append(event)
                                            require(sum(x.flow_controlled_length for x in held) <= 4096,
                                                'Peer exceeded its unreplenished stream receive credit')
                                        elif isinstance(event, (h2.events.StreamEnded, h2.events.StreamReset, h2.events.ConnectionTerminated)):
                                            raise AssertionError('Slow download ended/reset while receive credit was withheld')
                                    control = protocol.data_to_send()
                                    if control: transport.sendall(control)
                                samples.append(process_snapshot(service.process))
                            transport.settimeout(previous_timeout)
                            self.evidence.check('slow-download-withheld-credit-observed',
                                0 < sum(x.flow_controlled_length for x in held) <= 4096
                                and len(observed) < len(data) and not ended,
                                bytesBeforeHold=bytes_before, bytesAfterHold=len(observed),
                                heldFlowControlledBytes=sum(x.flow_controlled_length for x in held))
                            self.evidence.event('receive-window-withheld', seconds=time.monotonic() - start,
                                initialWindow=4096, bytesBeforeHold=bytes_before, bytesAfterHold=len(observed),
                                socketReadDuringHold=True, processSamples=samples)
                            # Only now does the client release stream flow-control credit.
                            for event in held: protocol.acknowledge_received_data(event.flow_controlled_length, event.stream_id)
                            held.clear(); stalled = True
                        pending = protocol.data_to_send()
                        if pending: transport.sendall(pending)
                    expected = hashlib.sha256(data).hexdigest()
                    self.evidence.check('slow-download-exact-complete-content', stalled and bytes(observed) == data
                        and response_headers.get('content-length') == str(len(data))
                        and response_headers.get('content-sha256') == expected, bytes=len(observed), sha256=hashlib.sha256(observed).hexdigest())
            self.evidence.check('host-continues-after-window-stall', service.request('GET', '/v1/items', token=token)[0] == 200)

    def shutdown_upload(self, path):
        config, token, sql = self.setup(path)
        data = b'bounded shutdown recovery\n' * 8192
        with self.service(config, path / 'service') as service:
            item = self.seed_item(service, token, 'shutdown-parent')
            transport = self.open_upload(service, token, item, len(data), data[:65536], 'shutdown-upload')
            try:
                staged = Path(config['blobDirectory']) / 'staging'
                eventually(lambda: any(p.stat().st_size > 0 for p in staged.glob('*.part')), 'active upload before SIGTERM')
                self.no_attachment(sql, item)
                self.evidence.event('sigterm-during-active-upload', process=process_snapshot(service.process), sentBytes=65536, declaredBytes=len(data))
                os.kill(service.process.pid, signal.SIGTERM)
                # The client stays open and withholds the rest. Server cancellation,
                # not client EOF, must allow the process's own safe join to finish.
                exit_code = service.process.wait(timeout=15)
                self.evidence.check('shutdown-joins-with-open-incomplete-client', exit_code == 0, exitCode=exit_code)
            finally:
                transport.close()
        self.evidence.check('shutdown-public-receipt', service.shutdown.get('serverState') == 'Disposed'
            and service.shutdown['databaseResourcesReleased'] and service.shutdown['consumerResourcesReleased'], receipt=service.shutdown)
        self.no_attachment(sql, item)
        with self.service(config, path / 'restarted-service') as service:
            status, _, _ = service.request('PUT', f'/v1/items/{item}/attachment', data,
                {'Content-Type': 'application/octet-stream', 'Idempotency-Key': 'shutdown-upload', 'If-Match': '1'}, token)
            downloaded = service.request('GET', f'/v1/items/{item}/attachment', token=token)
            self.evidence.check('shutdown-restart-protected-retry', status == 200 and downloaded[0] == 200 and downloaded[1] == data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifact-directory', type=Path, required=True)
    parser.add_argument('--mode', choices=['jit', 'aot'], required=True)
    parser.add_argument('--provider', choices=['sqlite', 'postgresql'], required=True)
    parser.add_argument('--postgres-bin', type=Path)
    parser.add_argument('--certificate', type=Path, required=True)
    parser.add_argument('--private-key', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    artifact = args.artifact_directory.resolve()
    executable = artifact / ('NativeWeave.Reference.TaskService.dll' if args.mode == 'jit' else 'NativeWeave.Reference.TaskService')
    require(executable.is_file(), 'Actual B21 artifact is required')
    require(args.provider != 'postgresql' or args.postgres_bin is not None, 'Explicit PostgreSQL18 binary directory is required')
    before = {str(path.relative_to(artifact)): sha(path) for path in sorted(artifact.rglob('*')) if path.is_file()}
    command = ['dotnet', str(executable)] if args.mode == 'jit' else [str(executable)]
    evidence = Evidence(args.output.resolve(), {'kind': 'external slow-client and shutdown cases', 'mode': args.mode,
        'provider': args.provider, 'executableSha256': sha(executable), 'artifacts': before,
        'controllerSha256': sha(Path(__file__)), 'harnessSha256': sha(Path(__file__).with_name('harness.py')),
        'faultHelperSha256': sha(Path(__file__).with_name('faults.py')), 'tlsCertificateSha256': sha(args.certificate),
        'fullB22Acceptance': False, 'performanceMeasurement': False, 'aggregateManagedRootClaim': False})
    exit_code = 0
    expected_cases = ['slow-upload', 'slow-download-window', 'active-upload-shutdown']
    try:
        context = PostgresCluster(args.postgres_bin, evidence.output / 'postgres') if args.provider == 'postgresql' else contextlib.nullcontext(None)
        with context as cluster:
            suite = Transports(command, args.provider, evidence, cluster=cluster, tls=(args.certificate.resolve(), args.private_key.resolve()))
            for name, case in [('slow-upload', suite.slow_upload), ('slow-download-window', suite.slow_download), ('active-upload-shutdown', suite.shutdown_upload)]:
                try:
                    with evidence.case(name) as path:
                        case(path)
                        logs = list(path.rglob('service.log'))
                        witness = 'B21_RUNTIME dynamic=' + ('True' if args.mode == 'jit' else 'False')
                        evidence.check('actual-runtime-witness', bool(logs) and all(witness in log.read_text() for log in logs))
                except Exception as error:
                    print(name + ' failed: ' + type(error).__name__, flush=True); exit_code = 1
        after = {str(path.relative_to(artifact)): sha(path) for path in sorted(artifact.rglob('*')) if path.is_file()}
        require(after == before, 'Executed product artifacts changed during the run')
        evidence.record['artifactsUnchanged'] = True
    except BaseException as error:
        exit_code = 1
        evidence.record['outerFailure'] = {'type': type(error).__name__,
            'interrupted': isinstance(error, (KeyboardInterrupt, SystemExit))}
        print('transport controller failed: ' + type(error).__name__, flush=True)
    finally:
        complete = [case['id'] for case in evidence.record['cases']] == expected_cases
        evidence.record['requiredCases'] = expected_cases
        evidence.record['status'] = 'passed' if complete and exit_code == 0 \
            and evidence.record.get('artifactsUnchanged') is True \
            and all(case['status'] == 'passed' for case in evidence.record['cases']) else 'incomplete'
        evidence.record['longDurationAcceptance'] = {'steady30m': 'not-run', 'regression24h': 'not-run', 'release72h': 'not-run'}
        # Publish one final atomic receipt only after all strict predicates are
        # decided. No intermediate helper may durably mark partial cases passed.
        try:
            evidence.save()
        finally:
            evidence.timeline.close()
    print(json.dumps({'status': evidence.record['status'], 'receipt': str(evidence.output / 'receipt.json')}), flush=True)
    return exit_code if evidence.record['status'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
