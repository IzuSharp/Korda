#!/usr/bin/env python3
"""Actual Linux process contention and crash/resume for the product C# Native AOT wrapper."""
import argparse, hashlib, json, os
from pathlib import Path
import signal, sqlite3, subprocess, tempfile, time, urllib.parse, zlib

parser=argparse.ArgumentParser()
parser.add_argument('--postgres-bin',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
parser.add_argument('command',nargs=argparse.REMAINDER)
args=parser.parse_args(); command=args.command[1:] if args.command[:1]==['--'] else args.command
assert command and os.name=='posix'
args.output.mkdir(parents=True,exist_ok=True); observations=[]
executable=Path(command[-1]).resolve()
inputs={str(executable):hashlib.file_digest(executable.open('rb'),'sha256').hexdigest()}
for native in executable.parent.rglob('libnw_database_*.so'):
    inputs[str(native)]=hashlib.file_digest(native.open('rb'),'sha256').hexdigest()
observations.append({'actual_consumer_command':command,'input_sha256':inputs})
base=os.environ['NW_TEST_POSTGRES']; pg=args.postgres_bin/'psql'
def sql(uri,text):
    result=subprocess.run([pg,'-X','-A','-t','-v','ON_ERROR_STOP=1','-d',uri,'-c',text],capture_output=True,text=True,timeout=10)
    if result.returncode: raise RuntimeError(result.stderr)
    return result.stdout.strip()
def database(name):
    sql(base,'CREATE DATABASE '+name); parts=urllib.parse.urlsplit(base); return urllib.parse.urlunsplit(parts._replace(path='/'+name))
def start(mode,provider,connection,label):
    log=(args.output/(label+'.log')).open('w')
    process=subprocess.Popen([*command,mode,provider,str(connection)],stdout=log,stderr=subprocess.STDOUT)
    return process,log,label
def finish(child):
    process,log,label=child
    try: code=process.wait(timeout=60)
    finally: log.close()
    contents=(args.output/(label+'.log')).read_text()
    assert code==0,(label,code,contents)
    return contents
def until(test,seconds=10):
    deadline=time.monotonic()+seconds
    while time.monotonic()<deadline:
        value=test()
        if value:return value
        time.sleep(.01)
    raise AssertionError('Actual process boundary was not observed within its budget')
with tempfile.TemporaryDirectory(prefix='nw-b08-process-') as directory:
    directory=Path(directory)
    path=directory/'contention.db'; holder=sqlite3.connect(path)
    holder.execute('PRAGMA journal_mode=WAL');holder.execute('BEGIN IMMEDIATE')
    first=start('contend','sqlite',path,'sqlite-contention-1');second=start('contend','sqlite',path,'sqlite-contention-2')
    time.sleep(.25);assert first[0].poll() is None and second[0].poll() is None
    holder.commit();holder.close()
    results=[finish(first),finish(second)]
    assert sum('applied=1 ' in value for value in results)==1 and sum('applied=0 ' in value for value in results)==1
    with sqlite3.connect(path) as observer:
        assert observer.execute('SELECT count(*) FROM _sqlx_migrations').fetchone()[0]==1
        assert observer.execute('SELECT count(*) FROM worker_state').fetchone()[0]==1
        assert observer.execute('SELECT provider,phase,length(sql_hash),length(bundle_hash) FROM _nativeweave_migration_phases').fetchone()==(1,'applied',32,32)
    observations.append({'provider':'SQLite','case':'cross-process-contention','two_successful_processes':True,'applied_counts':[int('applied=1 ' in x) for x in results]})
    path=directory/'crash.db';child=start('crash','sqlite',path,'sqlite-crashed');wal=Path(str(path)+'-wal')
    try:
        size=until(lambda:wal.stat().st_size if wal.exists() and wal.stat().st_size>131072 and child[0].poll() is None else None)
        child[0].kill();assert child[0].wait(timeout=5)==-signal.SIGKILL
    finally:child[1].close()
    with sqlite3.connect(path) as observer:
        assert observer.execute("SELECT count(*) FROM sqlite_schema WHERE name IN ('worker_state','_sqlx_migrations','_nativeweave_migration_phases')").fetchone()[0]==0
    resumed=finish(start('crash','sqlite',path,'sqlite-resumed'));assert 'applied=1 ' in resumed
    repeated=finish(start('crash','sqlite',path,'sqlite-repeated'));assert 'applied=0 ' in repeated
    with sqlite3.connect(path) as observer: assert observer.execute('SELECT count(*) FROM worker_state').fetchone()[0]==10000000
    observations.append({'provider':'SQLite','case':'SIGKILL-after-uncommitted-WAL-spill','observed_wal_bytes':size,'complete_rollback':True,'identical_new_process_resume':True,'third_process_idempotent':True})
    uri=database('b08_process_contention');name='b08_process_contention';lock=0x3d32ad9e*zlib.crc32(name.encode())
    holding=subprocess.Popen([pg,'-X','-A','-t','-v','ON_ERROR_STOP=1','-d',uri,'-c',f'SELECT pg_advisory_lock({lock}); SELECT pg_sleep(0.8)'],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    until(lambda:sql(base,"SELECT count(*) FROM pg_locks l JOIN pg_database d ON d.oid=l.database WHERE d.datname='b08_process_contention' AND l.locktype='advisory'")=='1')
    first=start('contend','postgresql',uri,'postgres-contention-1');second=start('contend','postgresql',uri,'postgres-contention-2')
    waiting=until(lambda:int(sql(base,"SELECT count(*) FROM pg_stat_activity WHERE datname='b08_process_contention' AND wait_event='advisory'")))
    assert holding.wait(timeout=5)==0
    results=[finish(first),finish(second)];assert sum('applied=1 ' in x for x in results)==1 and sum('applied=0 ' in x for x in results)==1
    assert sql(uri,'SELECT count(*) FROM _sqlx_migrations')=='1' and sql(uri,'SELECT count(*) FROM worker_state')=='1'
    observations.append({'provider':'PostgreSQL18','case':'cross-process-contention','observed_advisory_waiters':waiting,'one_confirmed_ledger_entry':True})
    uri=database('b08_process_crash');child=start('crash','postgresql',uri,'postgres-crashed')
    try:
        backend=until(lambda:sql(base,"SELECT pid FROM pg_stat_activity WHERE datname='b08_process_crash' AND state='active' AND query LIKE 'CREATE TABLE worker_state%'") or None)
        child[0].kill();assert child[0].wait(timeout=5)==-signal.SIGKILL
    finally:child[1].close()
    until(lambda:sql(base,f'SELECT count(*) FROM pg_stat_activity WHERE pid={int(backend)}')=='0')
    assert sql(uri,"SELECT to_regclass('worker_state') IS NULL")=='t'
    assert sql(uri,'SELECT count(*) FROM _sqlx_migrations')=='0'
    assert sql(uri,'SELECT count(*) FROM _nativeweave_migration_phases')=='0'
    resumed=finish(start('crash','postgresql',uri,'postgres-resumed'));assert 'applied=1 ' in resumed
    repeated=finish(start('crash','postgresql',uri,'postgres-repeated'));assert 'applied=0 ' in repeated
    assert sql(uri,'SELECT count(*) FROM worker_state')=='1'
    assert sql(base,"SELECT count(*) FROM pg_locks l JOIN pg_database d ON d.oid=l.database WHERE d.datname='b08_process_crash' AND l.locktype='advisory'")=='0'
    observations.append({'provider':'PostgreSQL18','case':'SIGKILL-during-transactional-script','observed_backend_pid':int(backend),'backend_and_advisory_lock_retired':True,'identical_new_process_resume':True,'third_process_idempotent':True})
for path in args.output.glob('*.log'):
    observations.append({'log':path.name,'sha256':hashlib.file_digest(path.open('rb'),'sha256').hexdigest()})
(args.output/'summary.json').write_text(json.dumps(observations,indent=2)+'\n')
print('B08 Linux actual process contention, SIGKILL recovery and identical replay passed for SQLite and PostgreSQL18')
