#!/usr/bin/env python3
"""B10 Linux component acceptance using a previously verified, immutable facade feed."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import shlex
import shutil
import subprocess
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parents[2]
PIN = 'c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9'
p = argparse.ArgumentParser()
p.add_argument('--feed', type=Path, required=True)
p.add_argument('--feed-source', type=Path, required=True)
p.add_argument('--feed-candidate', required=True)
p.add_argument('--design', type=Path, required=True)
p.add_argument('--sdk', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
OUT = a.output.resolve()
OUT.mkdir(parents=True, exist_ok=False)
FEED = a.feed.resolve()
env = os.environ.copy()
flags = ['-m:1', '-nr:false', '-p:UseSharedCompilation=false']
commands = []
rows = []

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def save(name, value):
    (OUT / name).write_text(json.dumps(value, indent=2) + '\n')

def git(*args, root=ROOT):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()

def sources():
    names = git('ls-files', '--', 'src/Query.Generator', 'tests/Query.Generator',
                'samples/api/query-generator', 'docs/guides/query-generator.md',
                'global.json', 'Directory.Build.props', 'Directory.Build.targets').splitlines()
    return {name: sha(ROOT / name) for name in names}

def run(name, command, cwd=ROOT, environment=env):
    log = OUT / f'{len(commands) + 1:02d}-{name}.log'
    print('RUN ' + name, flush=True)
    start = time.monotonic()
    with log.open('w') as stream:
        result = subprocess.run([str(x) for x in command], cwd=cwd, env=environment,
                                stdout=stream, stderr=subprocess.STDOUT, timeout=900)
    commands.append(dict(name=name, command=[str(x) for x in command], cwd=str(cwd),
                         exit_code=result.returncode, seconds=round(time.monotonic() - start, 3),
                         log=log.name, log_sha256=sha(log)))
    save('commands.json', commands)
    if result.returncode:
        raise RuntimeError(f'{name} failed: {log}')
    return log.read_text().strip()

initial = sources()
commit = git('rev-parse', 'HEAD')
save('source-inputs.json', dict(commit=commit, sha256=initial))
try:
    if platform.system() != 'Linux' or platform.machine() != 'x86_64':
        raise RuntimeError('Linux x64 component row required.')
    if git('status', '--porcelain'):
        raise RuntimeError('Commit the candidate before collecting immutable evidence.')
    if git('rev-parse', 'HEAD', root=a.design) != PIN:
        raise RuntimeError('Exact locked Design checkout required.')
    # Reusing accepted B13 packages does not permit a different generator candidate.
    generator_names = git('ls-files', '--', 'src/Query.Generator').splitlines()
    for name in generator_names:
        data = subprocess.check_output(['git', 'show', a.feed_candidate + ':' + name], cwd=a.feed_source)
        if hashlib.sha256(data).hexdigest() != initial[name]:
            raise RuntimeError('The feed generator source differs: ' + name)
    package_hashes = {p.name: sha(p) for p in FEED.glob('*.nupkg')}
    if len(package_hashes) < 10:
        raise RuntimeError('The complete previously verified facade feed is required.')
    with zipfile.ZipFile(FEED / 'NativeWeave.Database.Sqlite.0.1.0-preview.1.nupkg') as z:
        native_sha = hashlib.sha256(z.read('runtimes/linux-x64/native/libnw_database_sqlite.so')).hexdigest()
    save('feed-inputs.json', dict(source=str(a.feed_source), candidate=a.feed_candidate,
         package_sha256=package_hashes, native_sha256=native_sha, reused_acceptance='B13 Linux immutable package feed'))
    run('dotnet-identity', ['dotnet', '--info'])
    run('diff-check', ['git', 'diff', '--check'])
    for label, project in [('component', 'tests/Query.Generator/Query.Generator.Tests.csproj'),
                           ('api', 'tests/Query.Generator/ApiCheck/ApiCheck.csproj')]:
        run(label + '-restore', ['dotnet', 'restore', project, '--locked-mode', *flags])
        run(label + '-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *flags])
    run('component', ['dotnet', ROOT / 'tests/Query.Generator/bin/Release/net11.0/NativeWeave.Query.Generator.Tests.dll'])
    run('exact-api-diagnostics-incremental', ['dotnet', ROOT / 'tests/Query.Generator/ApiCheck/bin/Release/net11.0/NativeWeave.Query.Generator.ApiCheck.dll', ROOT, a.design, a.sdk])
    traps = OUT / 'no-rust'; traps.mkdir()
    trap_log = OUT / 'rust-invocations.txt'
    for tool in ['cargo', 'rustc', 'rustup']:
        script = traps / tool
        script.write_text('#!/bin/sh\nprintf "%s\\n" "$0 $*" >> ' + shlex.quote(str(trap_log)) + '\nexit 97\n')
        script.chmod(0o755)
    consumer_env = env | dict(NUGET_PACKAGES=str(OUT / 'fresh-nuget-cache'), CARGO_HOME=str(OUT / 'unused-cargo-home'),
                              RUSTUP_HOME=str(OUT / 'unused-rustup-home'), PATH=str(traps) + os.pathsep + env['PATH'])
    for mode in ['generated', 'manual']:
        consumer = Path(tempfile.mkdtemp(prefix='B10-' + mode + '-', dir=ROOT.parent))
        sample = ROOT / 'samples/api/query-generator'
        for name in ['Program.cs', 'QueryGenerator.Sample.csproj']:
            shutil.copy2(sample / name, consumer / name)
        shutil.copy2(ROOT / 'global.json', consumer / 'global.json')
        config = consumer / 'NuGet.Config'
        config.write_text('<configuration><packageSources><clear/><add key="local" value="' + str(FEED) + '"/></packageSources></configuration>')
        project = consumer / 'QueryGenerator.Sample.csproj'
        mode_args = ['-p:ManualMappings=true'] if mode == 'manual' else []
        for revision in ['original', 'edited']:
            if revision == 'edited':
                program = consumer / 'Program.cs'; text = program.read_text()
                replacements = {
                    'labels (Id INTEGER PRIMARY KEY, Text TEXT NOT NULL)': 'labels (Id INTEGER PRIMARY KEY, Text TEXT NOT NULL, Rank INTEGER NOT NULL)',
                    "labels VALUES (1,'second')": "labels VALUES (1,'second',9)",
                    'new Label(1, "second")': 'new Label(1, "second", 9)',
                    'record Label([property: WeaveKey] long Id, string Text);': 'record Label([property: WeaveKey] long Id, string Text, long Rank);',
                    'internal static readonly Table<Label> Table': 'private static readonly Column<Label, long> Rank = Builder.Column("Rank", Codecs.Int64);\n    internal static readonly Table<Label> Table',
                    'new Label(row.Get(Id), row.Get(Text))': 'new Label(row.Get(Id), row.Get(Text), row.Get(Rank))',
                    'row.Set(Text, value.Text);': 'row.Set(Text, value.Text); row.Set(Rank, value.Rank);'
                }
                for old, new in replacements.items():
                    if text.count(old) != 1:
                        raise RuntimeError('Expected exactly one ordinary DTO edit target: ' + old)
                    text = text.replace(old, new)
                program.write_text(text)
            label = mode + '-' + revision
            run(label + '-restore', ['dotnet', 'restore', project, '--configfile', config, *flags, *mode_args], consumer, consumer_env)
            run(label + '-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *flags, *mode_args], consumer, consumer_env)
            dll = consumer / 'bin/Release/net11.0/QueryGenerator.Sample.dll'
            jit_result = run(label + '-jit', ['dotnet', dll], consumer, consumer_env)
            jit_native = list((consumer / 'bin/Release/net11.0').rglob('libnw_database_sqlite.so'))
            if not jit_native or any(sha(path) != native_sha for path in jit_native):
                raise RuntimeError('The JIT output changed native asset identity.')
            publish = OUT / (label + '-aot')
            run(label + '-restore-aot', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', '--configfile', config, *flags, *mode_args], consumer, consumer_env)
            run(label + '-publish-aot', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '--no-restore', '-o', publish, *flags, *mode_args], consumer, consumer_env)
            executable = publish / 'QueryGenerator.Sample'
            aot_result = run(label + '-aot', [executable, '--require-aot'], consumer, consumer_env)
            aot_native = list(publish.rglob('libnw_database_sqlite.so'))
            if not aot_native or any(sha(path) != native_sha for path in aot_native):
                raise RuntimeError('The AOT output changed native asset identity.')
            if aot_result != jit_result:
                raise RuntimeError('JIT/AOT metadata/results differ.')
            row = dict(mode=mode, revision=revision, consumer=str(consumer), result=jit_result,
                       managed_sha256=sha(dll), aot_sha256=sha(executable), native_sha256=native_sha,
                       input_sha256={x.name: sha(x) for x in consumer.iterdir() if x.is_file()}, project_references=0)
            rows.append(row); save('consumer-rows.json', rows)
    for revision in ['original', 'edited']:
        if len({r['result'] for r in rows if r['revision'] == revision}) != 1:
            raise RuntimeError('Generated and manual actual metadata/results differ.')
    if rows[0]['result'] == rows[1]['result']:
        raise RuntimeError('The mapped DTO edit did not alter canonical metadata.')
    if trap_log.exists():
        raise RuntimeError('Ordinary consumption attempted a Rust tool.')
    if initial != sources() or package_hashes != {p.name: sha(p) for p in FEED.glob('*.nupkg')}:
        raise RuntimeError('Verification changed source or the immutable package feed.')
    save('status.json', dict(result='passed', task='B10', platform='linux-x64', candidate=commit, design_commit=PIN,
         commands=len(commands), generated_manual_jit_aot_rows=8, two_entities=True, ordinary_mapped_dto_edit=True,
         unchanged_native_sha256=native_sha, no_rust_tool_invocations=True, other_platforms='not executed',
         retirement_evidence='B12/B13 accepted actual Host materialization; all sample owners await disposal',
         utc=datetime.datetime.now(datetime.timezone.utc).isoformat()))
    print('PASS B10 actual generated/manual ordinary facade JIT/AOT', flush=True)
except Exception as failure:
    save('status.json', dict(result='failed', task='B10', platform='linux-x64', candidate=commit,
                            error=str(failure), completed_commands=len(commands)))
    raise
