#!/usr/bin/env python3
"""Prepare pinned campaign binaries; all compilation ends before measurement."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

from campaign import command,save
from observe import sha,utc

ROOT=Path(__file__).resolve().parents[3]
HERE=Path(__file__).resolve().parent
BASELINE='771bc77cbd68d3f932b99ed2497c0697bbba5f02'
BASELINE_RUN='45356659607'
PACKAGE_HASHES={'postgresql':'458c115e12d65ca0719f542ea262aeb3347f5bc3269f99d5ba14f3e36c038e0f',
                'sqlite':'584d46aa5b08b1e0af46a454ac9f41704c28fe502884017e856ad1b66ca53ef5'}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--work',type=Path,required=True,help='Fresh build root outside the repository')
    args=parser.parse_args();output=args.output.resolve();work=args.work.resolve()
    if ROOT==work or ROOT in work.parents:raise ValueError('Consumers must be outside source tree')
    output.mkdir(parents=True,exist_ok=True);work.mkdir(parents=True,exist_ok=False)
    if subprocess.check_output(['git','status','--porcelain'],cwd=ROOT):raise ValueError('Commit the source before bootstrap')
    source=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    lock=json.loads((ROOT/'native/dependencies.lock.json').read_text())
    env=dict(os.environ);env.update(NUGET_PACKAGES=str(work/'support-packages'),DOTNET_CLI_TELEMETRY_OPTOUT='1',
        DOTNET_NOLOGO='true',DOTNET_PROCESSOR_COUNT='2',MSBUILDDISABLENODEREUSE='1')
    log=output/'build-logs';log.mkdir()
    command([sys.executable,ROOT/'eng/cpp_toolchain.py'],log/'toolchain.log',env,timeout=1800)
    toolroot=ROOT/'artifacts/toolchain'
    llvm=toolroot/('llvm-'+lock['toolchain']['llvm']['version'])
    cmake=toolroot/('cmake-'+lock['toolchain']['cmake']['version'])
    ninja=toolroot/('ninja-'+lock['toolchain']['ninja']['version'])
    env['PATH']=os.pathsep.join([str(llvm/'bin'),str(cmake/'bin'),str(ninja),env['PATH']])
    clang=llvm/'bin/clang';clangpp=llvm/'bin/clang++'
    env.update(CC=str(clang),CXX=str(clangpp),NW_POSTGRES_BIN=str(ROOT/'artifacts/postgres-tools/install/bin'),
        CMAKE_PREFIX_PATH=str(ROOT/'artifacts/postgres-tools/install'),CMAKE_BUILD_PARALLEL_LEVEL='4')
    command(['bash',ROOT/'eng/postgres-tools.sh','full'],log/'postgres-build.log',env,timeout=1800)
    command([sys.executable,ROOT/'eng/build.py','native'],log/'native-build.log',env,timeout=1800)
    baseline=work/'baseline'
    for provider in ('postgresql','sqlite'):
        target=baseline/provider
        command(['gh','run','download',BASELINE_RUN,'--repo','IzuSharp/Korda','-n','candidate-'+provider,'-D',target],
                log/('download-'+provider+'.log'),env)
        packages=list((target/'feed').glob('*.nupkg'))
        if len(packages)!=1 or sha(packages[0])!=PACKAGE_HASHES[provider]:raise ValueError('Original package identity differs')
        receipt=json.loads((target/'release.json').read_text())
        if receipt['source_commit']!=BASELINE:raise ValueError('Original candidate producer differs')
    dotnet=Path(shutil.which('dotnet')).resolve()
    candidate=work/'after-candidates';version='0.1.0-perf.dbperf01.'+source[:10]
    command([sys.executable,HERE.parent/'pack_candidate.py','--baseline',baseline,'--output',candidate,
             '--dotnet',dotnet,'--packages',work/'support-packages','--version',version],log/'candidate.log',env,timeout=1200)
    build=ROOT/'artifacts/cmake/linux-x64-release'
    sqlite_source=ROOT/'artifacts/dependencies/sqlite/sqlite-amalgamation-3510300'
    postgres=ROOT/'artifacts/postgres-tools/install'
    native=work/'native'
    native_arguments=[sys.executable,HERE/'native/build.py','--clang',clangpp,'--sqlite-source',sqlite_source,
        '--sqlite-archive',build/'libnw_sqlite_engine.a','--sqlite-build-dir',build,'--postgres-prefix',postgres,
        '--json-source',ROOT/'artifacts/dependencies/nlohmann-json/json']
    command([*native_arguments,'--main',HERE/'native/main.cpp','--output',native],
        log/'native-consumer.log',env)
    native_lifecycle=work/'native-lifecycle'
    command([*native_arguments,'--main',HERE/'native/correctness.cpp','--output',native_lifecycle],
        log/'native-lifecycle.log',env)
    fingerprint=work/'sqlite-fingerprint';fingerprint.mkdir()
    c=fingerprint/'fingerprint.c'
    c.write_text('#include <sqlite3.h>\n#include <stdio.h>\n'
        'static void quoted(const char *p){putchar(34);for(;*p;p++){unsigned char c=*p;if(c==34||c==92)putchar(92);if(c<32)printf("\\\\u%04x",c);else putchar(c);}putchar(34);}\n'
        'int main(void){fputs("{\\"sqlite_runtime_source_id\\":",stdout);quoted(sqlite3_sourceid());fputs(",\\"compile_options\\":[",stdout);'
        'for(int i=0;;i++){const char *v=sqlite3_compileoption_get(i);if(!v)break;if(i)putchar(44);quoted(v);}puts("]}");return 0;}\n')
    command([clang,'-std=c17','-O3','-DNDEBUG','-I',sqlite_source,c,build/'libnw_sqlite_engine.a','-ldl','-lm','-lpthread','-o',fingerprint/'probe'],
            log/'sqlite-fingerprint-build.log',env)
    observation=subprocess.check_output([str(fingerprint/'probe')],env=env)
    json.loads(observation);(fingerprint/'result.json').write_bytes(observation)
    sqlite_image=work/'sqlite-image'
    command([sys.executable,HERE/'dapper/build_sqlite_image.py','--product-root',ROOT,'--clang',clang,
        '--sqlite-source',sqlite_source,'--compile-commands',build/'compile_commands.json',
        '--reference-fingerprint',fingerprint/'result.json','--output',sqlite_image],log/'sqlite-image.log',env)
    executables={route:{} for route in ('before','native','after','dapper')}
    def stage_support(cache):
        shared=work/'support-packages';shared.mkdir(exist_ok=True)
        for path in cache.iterdir():
            if path.is_dir() and not path.name.lower().startswith('korda.') and not (shared/path.name).exists():
                (shared/path.name).symlink_to(path.resolve(),target_is_directory=True)
    for provider in ('postgresql','sqlite'):
        executables['native'][provider]=str(native/'db-perf-native')
        for route,feed,package_version in [('before',baseline/provider/'feed','0.1.0-preview.3'),('after',candidate/provider/'feed',version)]:
            destination=work/(route+'-'+provider)
            command([sys.executable,HERE/'korda/publish.py','--provider',provider,'--feed',feed,'--version',package_version,
                '--output',destination,'--dotnet',dotnet,'--clang',clang,'--support-cache',work/'support-packages'],log/(route+'-'+provider+'.log'),env)
            executables[route][provider]=str(destination/'out/DbPerf.Korda')
            stage_support(destination/'packages')
        destination=work/('dapper-'+provider)
        command([sys.executable,HERE/'dapper/publish.py','--provider',provider,'--output',destination,'--dotnet',dotnet,
            '--clang',clang,'--fallback-cache',work/'support-packages'],log/('dapper-'+provider+'.log'),env)
        executables['dapper'][provider]=str(destination/'publish/DbPerf.Dapper')
        stage_support(destination/'cache')
    # Re-measure original and final packages in this same VM. These short
    # allocation processes are distinct from all sustained performance windows.
    for phase,packages in (('before',baseline),('after',candidate)):
        command([sys.executable,HERE.parent/'allocations/run.py','--artifacts',packages,
            '--work',work/('allocations-'+phase),'--evidence',output/'allocations'/phase,
            '--dotnet',dotnet,'--clang-bin',llvm/'bin','--postgres-bin',postgres/'bin',
            '--support-cache',work/'support-packages'],log/('allocations-'+phase+'.log'),env,timeout=1500)
    command([sys.executable,HERE.parent/'allocations/compare.py','--before',output/'allocations/before',
        '--after',output/'allocations/after'],log/'allocation-comparison.log',env)
    cpus=sorted(os.sched_getaffinity(0))
    if len(cpus)<4:raise RuntimeError('Four available vCPUs are required for fixed client/server/observer separation')
    memusage=next((path for path in (Path('/lib/x86_64-linux-gnu/libmemusage.so'),Path('/usr/lib/x86_64-linux-gnu/libmemusage.so')) if path.is_file()),None)
    if memusage is None:raise RuntimeError('Install the glibc memusage companion profiler before measurement')
    # The dedicated VM permits a task-local cgroup; no controller or access
    # policy is disabled. Its counters retain CPU charged to exited backends.
    server_cgroup=Path('/sys/fs/cgroup')/('db-perf-01-'+os.environ.get('GITHUB_RUN_ID',str(os.getpid()))+'-'+os.environ.get('GITHUB_RUN_ATTEMPT','1'))
    command(['sudo','-n','mkdir',server_cgroup],log/'server-cgroup-create.log',env)
    if not (server_cgroup/'cpu.stat').is_file():raise RuntimeError('Task-owned cgroup v2 CPU accounting is required')
    config=dict(schema_version=1,source_commit=source,baseline_commit=BASELINE,baseline_run=BASELINE_RUN,
        executables=executables,executable_sha256={r:{p:sha(path) for p,path in values.items()} for r,values in executables.items()},
        native_lifecycle_executable=str(native_lifecycle/'db-perf-native'),native_lifecycle_sha256=sha(native_lifecycle/'db-perf-native'),
        baseline_packages={p:str(next((baseline/p/'feed').glob('*.nupkg'))) for p in ('postgresql','sqlite')},
        final_candidates={p:str(next((candidate/p/'feed').glob('*.nupkg'))) for p in ('postgresql','sqlite')},
        sqlite_native=str(sqlite_image/'libsqlite3.so'),postgres_bin=str(postgres/'bin'),memusage=str(memusage),memusage_sha256=sha(memusage),server_cgroup=str(server_cgroup),
        client_cpus=cpus[:2],server_cpus=[cpus[2]],observer_cpus=[cpus[3]],
        completion_utc=utc(),build_work=str(work),runtime_environment={'DOTNET_PROCESSOR_COUNT':'2'})
    save(output/'config.json',config)
    # Retain exact sources/receipts/packages/binaries, but no multi-GB compiler cache.
    retained=output/'inputs';retained.mkdir()
    for name,directory in [('baseline',baseline),('after-candidates',candidate),('sqlite-fingerprint',fingerprint),('sqlite-image',sqlite_image),('native',native),('native-lifecycle',native_lifecycle)]:
        shutil.copytree(directory,retained/name,ignore=shutil.ignore_patterns('*.o','__pycache__'))
    for route in ('before','after','dapper'):
        for provider in ('postgresql','sqlite'):
            directory=work/(route+'-'+provider)
            shutil.copytree(directory,retained/(route+'-'+provider),ignore=shutil.ignore_patterns('bin','obj','packages','cache','support-cache','dotnet-home','cli-home','__pycache__'))
    for phase in ('before','after'):
        for provider in ('postgresql','sqlite'):
            directory=work/('allocations-'+phase)/provider/'app'
            shutil.copytree(directory,retained/('allocations-'+phase)/provider,ignore=shutil.ignore_patterns('bin','obj','__pycache__'))
    save(output/'bootstrap.json',dict(source_commit=source,baseline_commit=BASELINE,completed_utc=utc(),
         config_sha256=sha(output/'config.json'),all_compilation_finished=True,performance_acceptance_claimed=False))
    print('BOOTSTRAP_COMPLETE',source,flush=True)


if __name__=='__main__':main()
