#!/usr/bin/env python3
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess

BASE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--suite', choices=['data', 'streaming'], required=True)
parser.add_argument('--attempt', default='v1')
args = parser.parse_args()
build = BASE / (args.suite + '-build-' + args.attempt)
out = BASE / (args.suite + '-gates-' + args.attempt)
out.mkdir(exist_ok=False)
assembly = 'Korda.Data.Tests' if args.suite == 'data' else 'Korda.Data.Streaming.Tests'
executable = build / 'aot' / assembly
sha = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()
build_receipt = json.loads((build / 'build-receipt.json').read_text())
assert sha(executable) == build_receipt['executableSha256']
gates = [('functional', []), ('scalar', ['--allocation-probe', '--allocation-regression']),
         ('configured-scalar', ['--configured-scalar-probe', '--allocation-regression']),
         ('owned-wire', ['--owned-wire-allocation-regression'])] if args.suite == 'data' else [
         ('allocation-zero', ['--allocation-gate-only']), ('owned-batch', ['--owned-batch-probe-only']),
         ('stable-owner', ['--stable-batch-only']), ('races-and-native-borrow', ['--allocation-probe-races'])]
receipt = {'scope': 'Separate fresh Native AOT processes preserve strict raw zero-allocation gate process histories.',
           'buildReceiptSha256': sha(build / 'build-receipt.json'), 'executableSha256': sha(executable),
           'runnerSha256': sha(__file__), 'records': []}
for name, flags in gates:
    command = [str(executable), str(out / name), '--require-aot', *flags]
    started = datetime.now(timezone.utc).isoformat()
    log_path = out / (name + '.log')
    with log_path.open('x') as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=120)
    receipt['records'].append({'name': name, 'argv': command, 'startedUtc': started,
        'finishedUtc': datetime.now(timezone.utc).isoformat(), 'exitCode': result.returncode,
        'logSha256': sha(log_path)})
    (out / 'commands.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(name, 'exit', result.returncode, flush=True)
    print(log_path.read_text()[-3000:], flush=True)
    result.check_returncode()
