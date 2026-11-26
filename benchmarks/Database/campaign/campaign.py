#!/usr/bin/env python3
"""Serial Linux campaign: correctness, calibration, eight effective 1800 s segments."""
import argparse
import ctypes
import json
import math
import os
from pathlib import Path
import platform
import pwd
import re
import shlex
import shutil
import socket
import statistics
import subprocess
import time

import observe

HERE=Path(__file__).resolve().parent
ROUTES=('before','native','after','dapper')
PROVIDERS=('postgresql','sqlite')
P99_US=250_000
ERROR_RATE=.001


def save(path,value):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_suffix(path.suffix+'.tmp')
    temporary.write_text(json.dumps(value,indent=2,ensure_ascii=False)+'\n');temporary.replace(path)


def command(argv,log,env=None,timeout=600):
    argv=list(map(str,argv));log=Path(log);log.parent.mkdir(parents=True,exist_ok=True)
    if log.exists():raise ValueError('Refuse to overwrite raw command log: '+str(log))
    with log.open('w') as stream:
        stream.write(json.dumps(dict(argv=argv,started_utc=observe.utc()))+'\n');stream.flush()
        result=subprocess.run(argv,stdout=stream,stderr=subprocess.STDOUT,env=env,timeout=timeout)
    if result.returncode:raise RuntimeError(f'Command exit={result.returncode}: {log}')


class Sqlite:
    """Use the actual matched SQLite image for fixture and plan inspection."""
    def __init__(self,image):
        self.lib=ctypes.CDLL(str(image));p=ctypes.c_void_p;i=ctypes.c_int;s=ctypes.c_char_p
        for name,args,result in [('sqlite3_open_v2',[s,ctypes.POINTER(p),i,s],i),('sqlite3_close',[p],i),
            ('sqlite3_prepare_v2',[p,s,i,ctypes.POINTER(p),ctypes.POINTER(s)],i),('sqlite3_step',[p],i),
            ('sqlite3_finalize',[p],i),('sqlite3_column_count',[p],i),('sqlite3_column_type',[p,i],i),
            ('sqlite3_column_text',[p,i],s),('sqlite3_bind_int64',[p,i,ctypes.c_int64],i),
            ('sqlite3_bind_text',[p,i,s,i,p],i),('sqlite3_bind_null',[p,i],i),('sqlite3_errmsg',[p],s)]:
            fn=getattr(self.lib,name);fn.argtypes=args;fn.restype=result

    def query(self,path,sql,parameters=()):
        db=ctypes.c_void_p();statement=ctypes.c_void_p();buffers=[]
        if self.lib.sqlite3_open_v2(str(path).encode(),ctypes.byref(db),2,None):raise RuntimeError('Cannot open SQLite fixture')
        try:
            if self.lib.sqlite3_prepare_v2(db,sql.encode(),-1,ctypes.byref(statement),None):
                raise RuntimeError(self.lib.sqlite3_errmsg(db).decode())
            for index,param in enumerate(parameters,1):
                value=param['value']
                if value is None:code=self.lib.sqlite3_bind_null(statement,index)
                elif param['type']=='text':
                    data=value.encode();buffers.append(data);code=self.lib.sqlite3_bind_text(statement,index,data,len(data),None)
                else:code=self.lib.sqlite3_bind_int64(statement,index,value)
                if code:raise RuntimeError('SQLite bind failed: '+str(code))
            rows=[]
            while True:
                code=self.lib.sqlite3_step(statement)
                if code==101:break
                if code!=100:raise RuntimeError(self.lib.sqlite3_errmsg(db).decode())
                rows.append([None if self.lib.sqlite3_column_type(statement,index)==5 else self.lib.sqlite3_column_text(statement,index).decode()
                             for index in range(self.lib.sqlite3_column_count(statement))])
            return rows
        finally:
            if statement.value:self.lib.sqlite3_finalize(statement)
            if db.value and self.lib.sqlite3_close(db):raise RuntimeError('SQLite fixture connection did not close')


class Campaign:
    def __init__(self,args):
        self.args=args;self.root=args.output.resolve();self.root.mkdir(parents=True,exist_ok=True)
        self.config=json.loads(args.config.read_text());self.manifest=HERE/'contract-v1.json'
        self.contract=json.loads(self.manifest.read_text());self.sqlite=Sqlite(self.config['sqlite_native'])
        self.pg_bin=Path(self.config['postgres_bin']);self.data=self.root/'postgres-data'
        self.fixture=self.root/'orders-pristine.sqlite';self.serial=0
        self.config_sha=observe.sha(args.config);self.contract_sha=observe.sha(self.manifest)
        self.runtime_path=self.root/'database-runtime.json'
        self.runtime=json.loads(self.runtime_path.read_text()) if self.runtime_path.exists() else None
        self.server_pid=None
        self.available_affinity=sorted(os.sched_getaffinity(0))
        groups=[self.config[key] for key in ('client_cpus','server_cpus','observer_cpus')]
        requested=[cpu for group in groups for cpu in group]
        if any(not group for group in groups) or len(requested)!=len(set(requested)) or not set(requested).issubset(self.available_affinity):
            raise RuntimeError('Client, server and observer require disjoint available CPU sets')
        os.sched_setaffinity(0,self.config['observer_cpus'])

    def pg_start(self,label):
        if self.runtime is None:raise RuntimeError('Prepare database first')
        cpus=','.join(map(str,self.config['server_cpus']))
        options=['-h','127.0.0.1','-p',str(self.runtime['port']),'-k','',
            '-c','fsync=on','-c','synchronous_commit=on','-c','full_page_writes=on',
            '-c','plan_cache_mode=force_custom_plan','-c','jit=off','-c','statement_timeout=5000',
            '-c','shared_buffers=256MB','-c','max_connections=64',
            '-c','log_statement='+('all' if label=='smoke' else 'none')]
        command(['taskset','-c',cpus,self.pg_bin/'pg_ctl','-D',self.data,'start','-l',self.root/(label+'-server.log'),
                 '-o',shlex.join(options),'-w','-t','30'],self.root/(label+'-start.log'))
        self.server_pid=int((self.data/'postmaster.pid').read_text().splitlines()[0])
        try:
            group=Path(self.config['server_cgroup'])
            # Existing background processes must move explicitly. Later backends
            # inherit the postmaster's group, before any workload starts.
            for pid in observe.descendants(self.server_pid):
                moved=subprocess.run(['sudo','-n','tee',str(group/'cgroup.procs')],input=str(pid)+'\n',text=True,capture_output=True)
                if moved.returncode and Path('/proc',str(pid)).exists():raise RuntimeError('Cannot account for PostgreSQL process: '+moved.stderr)
            observed=set(map(int,(group/'cgroup.procs').read_text().split()))
            if not set(observe.descendants(self.server_pid)).issubset(observed):raise RuntimeError('A PostgreSQL process is outside its CPU accounting group')
            save(self.root/(label+'-server-cgroup.json'),dict(path=str(group),pids=sorted(observed),cpu=observe.cgroup_cpu(group)))
        except BaseException:
            self.pg_stop(label+'-accounting-failure')
            raise

    def pg_stop(self,label):
        if self.server_pid is not None:
            command([self.pg_bin/'pg_ctl','-D',self.data,'stop','-m','fast','-w','-t','30'],self.root/(label+'-stop.log'))
            self.server_pid=None

    def pg_sql(self,sql):
        result=subprocess.run([str(self.pg_bin/'psql'),self.runtime['connection'],'-X','-At','-v','ON_ERROR_STOP=1','-c',sql],
                              stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,timeout=30)
        if result.returncode:raise RuntimeError('PostgreSQL fixture/plan inspection failed: '+result.stderr)
        return result.stdout

    def executable(self,route,provider):
        return Path(self.config['executables'][route][provider])

    def base_command(self,route,provider,connection,output):
        cmd=['taskset','-c',','.join(map(str,self.config['client_cpus'])),str(self.executable(route,provider))]
        if route=='native':cmd+=['--provider',provider]
        cmd+=['--manifest',str(self.manifest),'--connection',str(connection),'--output',str(output)]
        if route=='dapper' and provider=='sqlite':cmd+=['--sqlite-native',self.config['sqlite_native']]
        return cmd

    def prepare(self):
        if self.runtime_path.exists():raise ValueError('Prepared fixtures are immutable; choose a fresh campaign output')
        affinity=self.available_affinity
        environment=dict(utc=observe.utc(),uname=list(platform.uname()),affinity=affinity,
            client_cpus=self.config['client_cpus'],server_cpus=self.config['server_cpus'],observer_cpus=self.config['observer_cpus'],
            boot_id=Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
            cpuinfo=Path('/proc/cpuinfo').read_text(),meminfo=Path('/proc/meminfo').read_text(),
            machine_id_sha256=observe.sha('/etc/machine-id'),
            observer_actual_affinity=sorted(os.sched_getaffinity(0)),
            source_commit=self.config['source_commit'],contract_sha256=self.contract_sha,config_sha256=self.config_sha,
            physical_host_exclusivity='not established; one dedicated GitHub Actions virtual machine; no builds during measurement',
            virtualization=subprocess.run(['systemd-detect-virt'],capture_output=True,text=True).stdout.strip())
        for name in ('cpu.max','memory.max','cpuset.cpus.effective'):
            path=Path('/sys/fs/cgroup')/name
            environment[name]=path.read_text().strip() if path.is_file() else None
        save(self.root/'environment.json',environment)
        command([self.pg_bin/'initdb','-D',self.data,'--auth=trust','--no-locale','--encoding=UTF8'],self.root/'initdb.log')
        with socket.socket() as listener:listener.bind(('127.0.0.1',0));port=listener.getsockname()[1]
        self.runtime=dict(port=port,connection=f'postgresql://{pwd.getpwuid(os.getuid()).pw_name}@127.0.0.1:{port}/postgres')
        save(self.runtime_path,self.runtime)
        self.pg_start('prepare')
        try:
            for provider in PROVIDERS:
                connection=self.runtime['connection'] if provider=='postgresql' else str(self.fixture)
                cmd=self.base_command('before',provider,connection,self.root/('init-'+provider+'.json'))+['--initialize']
                command(cmd,self.root/('init-'+provider+'.log'),timeout=600)
            snapshots={
                'postgresql':self.pg_sql('SELECT row_to_json(t) FROM (SELECT (SELECT count(*) FROM orders) orders,(SELECT count(*) FROM order_lines) lines,(SELECT count(*) FROM customers) customers,(SELECT count(*) FROM perf_state) states) t'),
                'sqlite':self.sqlite.query(self.fixture,'SELECT (SELECT count(*) FROM orders),(SELECT count(*) FROM order_lines),(SELECT count(*) FROM customers),(SELECT count(*) FROM perf_state)'),
                'sqlite_sha256':observe.sha(self.fixture)}
            save(self.root/'dataset.json',snapshots)
            for provider in PROVIDERS:
                _,immutable=self.persisted(provider,self.fixture if provider=='sqlite' else None)
                if immutable!=self.expected_counts():raise RuntimeError('Initialized dataset counts differ from the contract')
            self.capture_plans()
        finally:self.pg_stop('prepare')

    def capture_plans(self):
        plans=[]
        for case in self.contract['cases']:
            if case.get('steps'):continue
            sqlite=self.sqlite.query(self.fixture,'EXPLAIN QUERY PLAN '+case['sql']['sqlite'],case['parameters'])
            types=','.join('text' if p['type']=='text' else 'integer' if p['type']=='int32' else 'bigint' for p in case['parameters'])
            values=','.join("'"+p['value'].replace("'","''")+"'" if p['type']=='text' else str(p['value']) for p in case['parameters'])
            prepare='PREPARE dbperf_plan'+('('+types+')' if types else '')+' AS '+case['sql']['postgresql']+'; '
            explain='EXPLAIN (FORMAT JSON, COSTS TRUE) EXECUTE dbperf_plan'+('('+values+')' if values else '')+'; DEALLOCATE dbperf_plan;'
            postgres=self.pg_sql(prepare+explain)
            plans.append(dict(id=case['id'],name=case['name'],sql=case['sql'],parameters=case['parameters'],sqlite=sqlite,postgresql_raw=postgres,
                scope='Actual engine EXPLAIN of matched SQL and exact bound types; PostgreSQL force_custom_plan fixes parameter planning policy for every route.'))
        save(self.root/'query-plans.json',plans)

    def reset(self,provider,name):
        if provider=='postgresql':
            self.pg_sql('TRUNCATE perf_state; INSERT INTO perf_state SELECT id,0,1,NULL FROM generate_series(1,128) id;')
            self.pg_sql('VACUUM (ANALYZE) perf_state;')
            return self.runtime['connection'],None
        target=self.root/'databases'/(name+'.sqlite');target.parent.mkdir(exist_ok=True)
        if target.exists():raise ValueError('Do not overwrite a run database')
        shutil.copyfile(self.fixture,target)
        return str(target),target

    def expected_counts(self):
        dataset=self.contract['dataset'];per_tenant=dataset['orders_per_tenant'];tenants=dataset['tenants']
        return [tenants*per_tenant,tenants*sum(0 if i%5==0 else 1+i%3 for i in range(1,per_tenant+1)),dataset['customers']]

    def persisted(self,provider,local):
        counts='SELECT (SELECT count(*) FROM orders),(SELECT count(*) FROM order_lines),(SELECT count(*) FROM customers)'
        if provider=='sqlite':
            values=self.sqlite.query(local,'SELECT "Id","Counter","Version","Note" FROM perf_state ORDER BY "Id"')
            state=[dict(Id=int(row[0]),Counter=int(row[1]),Version=int(row[2]),Note=row[3]) for row in values]
            immutable=[int(value) for value in self.sqlite.query(local,counts)[0]]
        else:
            raw=self.pg_sql('SELECT row_to_json(t) FROM (SELECT * FROM perf_state ORDER BY "Id") t')
            state=[json.loads(line) for line in raw.splitlines() if line.strip()]
            immutable=[int(value) for value in self.pg_sql(counts).strip().split('|')]
        return state,immutable

    def state_matches(self,state,concurrency):
        initial=(0,1,None);allowed={initial}
        for case in self.contract['cases']:
            selects=[step['expected_state'] for step in case.get('steps',[]) if step['kind']=='select']
            if selects:
                value=selects[-1];allowed.add((value['counter'],value['version'],value['note']))
        if [row['Id'] for row in state]!=list(range(1,self.contract['dataset']['state_rows']+1)):return False
        return all((row['Counter'],row['Version'],row['Note']) in (allowed if row['Id']<=concurrency else {initial}) for row in state)

    @staticmethod
    def owners_retired(route,result):
        if route=='native':return result.get('owners_returned_to_zero') is True
        value=result.get('owner_retirement')
        if not isinstance(value,dict):return False
        if route in ('before','after'):
            return all(value.get(key)==0 for key in ('Sources','Sessions','Operations','Cursors','Buffers','Owners'))
        owners=all(value.get(key)==0 for key in ('active_physical_connections','active_orm_readers','active_transactions','disposal_failures')) and \
            all(value.get(opened)==value.get(closed) and value.get(opened) is not None for opened,closed in (
                ('opened_physical_connections','closed_physical_connections'),('created_orm_readers','closed_orm_readers'),('created_transactions','closed_transactions')))
        if result.get('session_mode')=='persistent':return owners
        pool=value.get('adapter_physical_pool',{})
        return owners and pool.get('active_leases')==0 and pool.get('acquisitions') is not None and pool.get('acquisitions')==pool.get('returns')

    def one(self,name,route,provider,*,mode='prepared',session='per-operation',concurrency=4,
            seconds=10,rate=0,operations=0,case='mixed',warmup=64,verify=False,empty=False,profile=False,allow_invalid=False,
            recovery_rate=None,recovery_after=None):
        folder=self.root/'runs'/name;connection,local=self.reset(provider,name)
        cmd=self.base_command(route,provider,connection,folder/'result.json')+[
            '--mode',mode,'--session-mode',session,'--concurrency',str(concurrency),'--warmup',str(warmup),
            '--seconds',str(seconds),'--rate',str(rate),'--operations',str(operations),'--case',case]
        if verify:cmd+=['--verify-only']
        if empty:cmd+=['--empty-driver']
        if recovery_rate is not None:cmd+=['--recovery-rate',str(recovery_rate),'--recovery-after',str(recovery_after)]
        env=dict(os.environ);env.pop('LD_PRELOAD',None);env.update(self.config.get('runtime_environment',{}))
        if profile:
            env['LD_PRELOAD']=self.config['memusage'];env['MEMUSAGE_NO_TIMER']='1'
        observer=observe.run(cmd,folder,seconds,server_pid=self.server_pid if provider=='postgresql' else None,
            server_cgroup=self.config['server_cgroup'] if provider=='postgresql' else None,env=env,operations=operations)
        result_path=folder/'result.json'
        result=json.loads(result_path.read_text()) if result_path.is_file() else None
        hard=[];capacity=[]
        def require(condition,reason):
            if not condition:hard.append(reason)
        require(result is not None,'missing_terminal_receipt')
        require(observer['status']!='watchdog_failure','external_watchdog')
        require(observer['exit_code'] in (0,2),'abnormal_process_exit')
        if result is not None:
            require(result.get('contract_sha256')==self.contract_sha,'contract_identity')
            require(self.owners_retired(route,result),'owner_retirement')
            require(result.get('provider')==provider and result.get('mode')==mode and result.get('session_mode')==session and result.get('concurrency')==concurrency,'execution_configuration')
            expected_sha=self.config['executable_sha256'][route][provider]
            require(result.get('executable_sha256')==expected_sha and observe.sha(self.executable(route,provider))==expected_sha,'executable_identity')
            require(result.get('errors_normalized',{}).get('validation',0)==0 and result.get('errors_normalized',{}).get('unknown',0)==0,'operation_oracle_or_unknown_failure')
            if route=='native':
                require(result.get('correctness_passed') is True,'verification_oracle')
                require(result.get('contract_unchanged_after_run') is True,'contract_changed_during_run')
                require(result.get('time_series_truncated') is False,'truncated_time_series')
            else:
                selected=len(self.contract['cases']) if case=='mixed' else 1
                checks=result.get('verification',[])
                require(len(checks)==selected*concurrency and all(row['matched'] for row in checks),'verification_oracle')
                require(result.get('contract_hash_stable') is True and result.get('library_hashes_stable') is True,'mutable_input_identity')
                require(result.get('setup_error') is None and result.get('close_hook_or_disposal_error') is None
                    and result.get('snapshot_error') is None and result.get('disposal_failures')==0,'setup_cleanup_or_observation_failure')
                require(result.get('by_second_truncated_events')==0,'truncated_time_series')
                if not verify:require(result.get('cpu_available') is True and result.get('resource_read_failures')==0,'client_resource_accounting')
            if observer['exit_code']!=0:capacity.append('nonzero_exit')
            if result.get('run_complete') is not True:capacity.append('incomplete_bounded_load')
        counts={} if result is None else result.get('counts',{})
        if not verify and result is not None:
            require(all(isinstance(counts.get(key),int) and counts[key]>=0 for key in ('planned','started','completed','failed','not_started','unfinished')),'invalid_request_counts')
            require(counts.get('planned')==counts.get('started',0)+counts.get('not_started',0),'planned_count_conservation')
            require(counts.get('started')==counts.get('completed',0)+counts.get('failed',0)+counts.get('unfinished',0),'terminal_count_conservation')
            phases=observer['phases']
            expected=['READY','START','END','DRAINED','IDLE_OPEN','IDLE_OPEN_DONE','CLOSED','IDLE','IDLE_DONE']
            require([item['name'] for item in phases]==expected,'phase_order_count')
            if [item['name'] for item in phases]==expected:
                observed_seconds=(phases[2]['monotonic_ns']-phases[1]['monotonic_ns'])/1e9
                require(abs(observed_seconds-result.get('window_seconds',0))<=.25,'phase_boundary_timing')
            if provider=='postgresql':
                cpu=observer.get('server_window',{})
                require(cpu.get('cgroup_user_seconds',-1)>=0 and cpu.get('cgroup_system_seconds',-1)>=0,'server_cgroup_cpu')
        final_state,immutable=self.persisted(provider,local)
        state_valid=self.state_matches(final_state,concurrency) and immutable==self.expected_counts()
        require(state_valid,'persisted_state_or_immutable_counts')
        valid=not hard and not capacity
        state_path=folder/'persisted-state.json';save(state_path,dict(state=final_state,immutable_table_counts=immutable,
            expected_immutable_counts=self.expected_counts(),valid=state_valid,
            validation_scope='All 128 state keys; inactive keys remain initial, active keys match a committed operation terminal; read-table counts match the deterministic seed. Full result digests are separately checked for every consumed business operation.'))
        p99=None if not result or verify else result.get('latency_total',{}).get('offered',{}).get('p99_us_upper')
        planned=counts.get('planned',0);failed=counts.get('failed',0)+counts.get('not_started',0)+counts.get('unfinished',0)
        error_rate=failed/planned if planned else 0
        passed_slo=valid and not verify and not empty and not profile and p99 is not None and p99<=P99_US and error_rate<=ERROR_RATE
        summary=dict(name=name,route=route,provider=provider,mode=mode,session=session,concurrency=concurrency,
            seconds=seconds,rate=rate,operations=operations,case=case,verify_only=verify,empty_driver=empty,native_heap_profile=profile,
            recovery_rate=recovery_rate,recovery_after=recovery_after,
            valid=bool(valid),slo_passed=passed_slo,slo=dict(maximum_error_rate=ERROR_RATE,maximum_p99_offered_us=P99_US),
            hard_integrity_failures=hard,capacity_failures=capacity,
            error_rate=error_rate,p99_offered_us=p99,
            successful_ops_per_second=0 if not result else result.get('successful_ops_per_second',result.get('success_ops_per_second',0)),
            counts=counts,contract_sha256=self.contract_sha,config_sha256=self.config_sha,source_commit=self.config['source_commit'],
            result_sha256=observe.sha(result_path) if result_path.is_file() else None,
            observer_sha256=observe.sha(folder/'observer.json'),persisted_state_sha256=observe.sha(state_path))
        if profile:
            output=re.sub(r'\x1b\[[0-9;]*m','',(folder/'stdout.log').read_text(errors='replace'))
            match=re.search(r'heap total: (\d+), heap peak: (\d+), stack peak: (\d+)',output)
            calls={name:{'calls':int(calls),'bytes':int(memory)} for name,calls,memory in re.findall(r'^\s*(malloc|calloc|realloc|free)\|\s*(\d+)\s+(\d+)',output,re.M)}
            summary['native_heap']=dict(status='measured' if match else 'unavailable',
                heap_total_bytes=int(match[1]) if match else None,heap_peak_bytes=int(match[2]) if match else None,calls=calls,
                library_sha256=observe.sha(self.config['memusage']),
                scope='Separate instrumented companion process, including setup/warmup/cleanup; intercepted malloc/calloc/realloc/free family only. Direct mmap/GC reservation and non-interposed allocation are excluded. Not substituted for uninstrumented long-run RSS/PSS or throughput.')
            require(match is not None and set(calls)=={'malloc','calloc','realloc','free'},'native_heap_profile_missing')
            require(observe.sha(self.config['memusage'])==self.config['memusage_sha256'],'native_heap_profiler_identity')
            valid=not hard and not capacity;summary['valid']=valid
        if recovery_rate is not None and result is not None:
            arrival=result.get('arrival_phases',[])
            require(len(arrival)==2,'arrival_phase_count')
            summary['arrival_phases']=arrival
        valid=not hard and not capacity
        summary['valid']=bool(valid);summary['slo_passed']=bool(summary['slo_passed'] and valid)
        save(folder/'summary.json',summary)
        if local and valid:
            for path in (local,Path(str(local)+'-wal'),Path(str(local)+'-shm')):
                if path.exists():path.unlink()
        if hard or (not valid and not allow_invalid):raise RuntimeError('Run failed its integrity gate: '+str(folder)+' '+','.join(hard+capacity))
        return summary

    def smoke(self):
        results=[]
        for provider in PROVIDERS:
            if provider=='postgresql':self.pg_start('smoke')
            try:
                for route in ROUTES:
                    for mode in ('dynamic','prepared'):
                        results.append(self.one(f'smoke-{provider}-{route}-{mode}',route,provider,mode=mode,concurrency=2,verify=True,warmup=0))
                    results.append(self.one(f'smoke-{provider}-{route}-fixed',route,provider,concurrency=2,seconds=3,rate=10,warmup=36))
                    results.append(self.lifecycle(route,provider))
            finally:
                if provider=='postgresql':self.pg_stop('smoke')
        save(self.root/'smoke.json',results)

    def lifecycle(self,route,provider):
        name=f'lifecycle-{provider}-{route}';folder=self.root/'runs'/name
        connection,local=self.reset(provider,name);output=folder/'result.json'
        cmd=self.base_command(route,provider,connection,output)
        expected=self.config['executable_sha256'][route][provider]
        if route=='native':
            cmd[3]=self.config['native_lifecycle_executable'];expected=self.config['native_lifecycle_sha256']
        else:cmd+=['--lifecycle-probe','--concurrency','2','--session-mode','per-operation']
        env=dict(os.environ);env.pop('LD_PRELOAD',None);env.update(self.config.get('runtime_environment',{}))
        observer=observe.run(cmd,folder,0,server_pid=self.server_pid if provider=='postgresql' else None,
            server_cgroup=self.config['server_cgroup'] if provider=='postgresql' else None,env=env,timeout_setup=90)
        failures=[]
        def require(condition,reason):
            if not condition:failures.append(reason)
        require(observer['exit_code']==0 and observer['status']=='process_exited','bounded_successful_exit')
        raw=json.loads(output.read_text()) if output.is_file() else {}
        require(raw.get('provider')==provider and raw.get('contract_sha256')==self.contract_sha,'provider_contract_identity')
        if route=='dapper':
            commands=Path(str(output)+'.commands.json')
            identity=json.loads(commands.read_text()) if commands.is_file() else {}
            require(identity.get('executable_sha256')==expected and identity.get('manifest_sha256')==self.contract_sha,'executed_image_identity')
            probes=raw.get('probes',[])
            expected_names={'two-worker-optimistic-conflict','early-enumerator-disposal','pre-canceled-token','in-flight-token-cancellation'}
            if provider=='sqlite':expected_names.add('sqlite-blocking-query-control')
            def documented_observation(row):
                return row.get('status')=='passed' or (provider=='sqlite' and row.get('name')=='in-flight-token-cancellation'
                    and row.get('status')=='cancellation_observed_without_asynchronous_sql_interruption_proof' and bool(row.get('limitation')))
            require(raw.get('status')=='completed' and len(probes)==len(expected_names)
                and {row['name'] for row in probes}==expected_names and all(documented_observation(row) for row in probes),'lifecycle_observations')
            require(self.owners_retired(route,dict(raw,session_mode='per-operation')),'owner_retirement')
        else:
            require(raw.get('executable_sha256')==expected,'executed_image_identity')
            require(raw.get('owners_returned_to_zero') is True,'owner_retirement')
            require(raw.get('status')=='passed_lifecycle_correctness' if route=='native' else raw.get('passed') is True,'lifecycle_observations')
        require(observe.sha(cmd[3])==expected and observe.sha(self.manifest)==self.contract_sha,'inputs_unchanged')
        state,immutable=self.persisted(provider,local)
        require(self.state_matches(state,0) and immutable==self.expected_counts(),'mixed_dataset_unchanged')
        summary=dict(name=name,route=route,provider=provider,valid=not failures,integrity_failures=failures,
            result_sha256=observe.sha(output) if output.is_file() else None,observer_sha256=observe.sha(folder/'observer.json'),
            contract_sha256=self.contract_sha,config_sha256=self.config_sha,executable_sha256=expected,
            performance_acceptance_claimed=False,
            scope='Separate lifecycle correctness. Keep the raw provider-specific cancellation limitations: Dapper SQLite VM interruption and Korda SQLite engine entry/interruption are unproven. Public cancellation/token/cleanup observations do not erase those gaps.')
        save(folder/'summary.json',summary)
        if failures:raise RuntimeError('Lifecycle integrity failed: '+name+' '+','.join(failures))
        if local:
            for path in (local,Path(str(local)+'-wal'),Path(str(local)+'-shm')):
                if path.exists():path.unlink()
        return summary

    def maximum_search(self,provider,route,base):
        """Bound and refine one observed operating point; never assert an untested maximum."""
        records=[];number=0;low=0;high=None;concurrency=base['concurrency']
        def trial(rate,label):
            nonlocal number
            number+=1
            result=self.one(f'maximum-{provider}-{route}-{number:02d}-{label}',route,provider,
                concurrency=concurrency,seconds=12,rate=rate,allow_invalid=True)
            records.append(result);return result
        rate=max(1,int(base['successful_ops_per_second']*.70))
        for attempt in range(4):
            result=trial(rate,'initial')
            if result['slo_passed']:low=rate;break
            high=rate
            if rate==1:break
            rate=max(1,rate//2)
        if low:
            if high is None:
                for attempt in range(5):
                    rate=max(low+1,int(low*1.35));result=trial(rate,'upper')
                    if not result['slo_passed']:high=rate;break
                    low=rate
            if high is not None:
                for attempt in range(4):
                    if high-low<=max(1,int(low*.04)):break
                    rate=(low+high)//2;result=trial(rate,'bisect')
                    if result['slo_passed']:low=rate
                    else:high=rate
        passing=sorted((r for r in records if r['slo_passed']),key=lambda r:r['rate'],reverse=True)
        confirmed=None
        for candidate in passing[:3]:
            repeat=[trial(candidate['rate'],'repeat') for _ in range(2)]
            if all(r['slo_passed'] for r in repeat):
                values=[candidate,*repeat]
                confirmed=dict(offered_rate=candidate['rate'],concurrency=concurrency,
                    successful_ops_per_second=[r['successful_ops_per_second'] for r in values],
                    median_successful_ops_per_second=statistics.median(r['successful_ops_per_second'] for r in values),
                    process_runs=[r['name'] for r in values]);break
        summary=dict(route=route,provider=provider,concurrency=concurrency,highest_confirmed=confirmed,
            upper_failing_offered_rate=high,upper_bound_found=high is not None,
            status='bounded_observed_point' if confirmed and high is not None else 'highest_tested_only' if confirmed else 'no_repeated_qualifying_point',
            scope='Adaptive offered-rate bracket and refinement at the highest observed closed-loop ramp concurrency, with three independent qualifying 12-second processes for the reported point. The concurrency grid is 1,2,4,8,16; this is an empirical operating point, not a proof of a global maximum or a replacement for the 1800-second common-load runs.',
            trials=[r['name'] for r in records])
        save(self.root/'maximum'/f'{provider}-{route}.json',summary)
        return records,summary

    def calibrate(self):
        self.pg_start('calibration')
        try:
            records=[];loads={};maximum=[]
            # Alternating route order and separate OS processes expose process noise.
            for provider in PROVIDERS:
                for repetition in range(3):
                    for route in ROUTES if repetition%2==0 else tuple(reversed(ROUTES)):
                        for case in ('numeric','stream-large'):
                            records.append(self.one(f'noise-{provider}-{repetition}-{route}-{case}',route,provider,
                                concurrency=1,session='persistent',case=case,operations=256,warmup=64))
                peaks={}
                for route in ROUTES:
                    for mode in ('dynamic','prepared'):
                        for session in ('persistent','per-operation'):
                            records.append(self.one(f'correct-{provider}-{route}-{mode}-{session}',route,provider,
                                concurrency=2,mode=mode,session=session,verify=True,warmup=0))
                    ramp=[]
                    for concurrency in (1,2,4,8,16):
                        sample=self.one(f'ramp-{provider}-{route}-{concurrency}',route,provider,concurrency=concurrency,seconds=10)
                        records.append(sample);ramp.append(sample)
                    # Fixed concurrency in the long comparison; ramp peaks remain separately visible.
                    base=next(item for item in ramp if item['concurrency']==4)
                    peaks[route]=base['successful_ops_per_second']
                    maximum_base=max(ramp,key=lambda item:item['successful_ops_per_second'])
                    trials,boundary=self.maximum_search(provider,route,maximum_base)
                    records.extend(trials);maximum.append(boundary)
                    records.append(self.one(f'empty-{provider}-{route}',route,provider,concurrency=4,seconds=5,
                        rate=max(1,int(peaks[route]*.6)),empty=True))
                    records.append(self.one(f'heap-{provider}-{route}',route,provider,concurrency=4,operations=144,warmup=36,profile=True))
                rate=max(1,int(min(peaks.values())*.60))
                # Freeze one sustainable offered load for all four columns.
                for attempt in range(5):
                    qualification=[]
                    for route in ROUTES:
                        sample=self.one(f'sustainable-{provider}-{attempt}-{route}',route,provider,concurrency=4,seconds=15,rate=rate,allow_invalid=True)
                        records.append(sample);qualification.append(sample)
                    if all(item['slo_passed'] for item in qualification):break
                    rate=max(1,int(rate*.65))
                else:raise RuntimeError('No common sustainable load met the frozen SLO')
                loads[provider]=dict(rate=rate,concurrency=4,mode='prepared',session='per-operation',seconds=1800,
                    case='mixed',warmup=360,qualification=[item['name'] for item in qualification],
                    maximum_error_rate=ERROR_RATE,maximum_p99_offered_us=P99_US)
                for route in ROUTES:
                    records.append(self.one(f'saturation-recovery-{provider}-{route}',route,provider,concurrency=4,seconds=30,
                        rate=max(2,int(peaks[route]*1.5)),recovery_rate=rate,recovery_after=10,allow_invalid=True))
                save(self.root/'calibration-progress.json',dict(records=records,loads=loads,maximum=maximum))
                if provider=='postgresql':self.pg_stop('calibration')
            save(self.root/'frozen-loads.json',dict(schema_version=1,source_commit=self.config['source_commit'],
                contract_sha256=self.contract_sha,config_sha256=self.config_sha,loads=loads,records=records,maximum=maximum))
        finally:self.pg_stop('calibration')

    def segment(self,index):
        schedule=[('postgresql',route) for route in ('before','native','after','dapper')]+[('sqlite',route) for route in ('native','after','before','dapper')]
        provider,route=schedule[index-1]
        frozen=json.loads((self.root/'frozen-loads.json').read_text())
        if frozen['contract_sha256']!=self.contract_sha or frozen['config_sha256']!=self.config_sha:
            raise RuntimeError('Calibration inputs changed')
        environment=json.loads((self.root/'environment.json').read_text())
        if environment['boot_id']!=Path('/proc/sys/kernel/random/boot_id').read_text().strip():
            raise RuntimeError('All eight segments must use the same VM boot')
        if provider=='postgresql':self.pg_start(f'segment-{index}')
        try:
            settings={key:value for key,value in frozen['loads'][provider].items()
                      if key in {'mode','session','concurrency','seconds','rate','case','warmup'}}
            result=self.one(f'long-{index:02d}-{provider}-{route}',route,provider,**settings)
            if not result['slo_passed']:raise RuntimeError('Long segment completed but did not meet the frozen SLO')
            raw=json.loads((self.root/'runs'/result['name']/'result.json').read_text())
            if raw.get('window_seconds',0)<1800 or not raw.get('can_qualify'):
                raise RuntimeError('A long segment did not contain 1800 effective seconds')
            result['accepted_effective_seconds']=raw['window_seconds'];result['boot_id']=environment['boot_id']
            result['environment_sha256']=observe.sha(self.root/'environment.json')
            result['frozen_loads_sha256']=observe.sha(self.root/'frozen-loads.json')
            save(self.root/'segments'/f'{index:02d}.json',result)
        finally:
            if provider=='postgresql':self.pg_stop(f'segment-{index}')

    def summarize(self):
        schedule=[('postgresql',route) for route in ('before','native','after','dapper')]+[('sqlite',route) for route in ('native','after','before','dapper')]
        paths=sorted((self.root/'segments').glob('*.json'));segments=[];failures=[]
        def require(condition,reason):
            if not condition:failures.append(reason)
        require([path.name for path in paths]==[f'{index:02d}.json' for index in range(1,9)],'exact_eight_segment_files')
        environment=json.loads((self.root/'environment.json').read_text())
        frozen=json.loads((self.root/'frozen-loads.json').read_text())
        require(environment['boot_id']==Path('/proc/sys/kernel/random/boot_id').read_text().strip(),'current_vm_boot')
        require(frozen.get('contract_sha256')==self.contract_sha and frozen.get('config_sha256')==self.config_sha,'frozen_input_identity')
        for index,(provider,route) in enumerate(schedule,1):
            path=self.root/'segments'/f'{index:02d}.json'
            if not path.is_file():continue
            segment=json.loads(path.read_text());segments.append(segment);label=f'{index:02d}'
            require((segment.get('provider'),segment.get('route'))==(provider,route),label+':provider_route')
            require(segment.get('name')==f'long-{index:02d}-{provider}-{route}',label+':run_name')
            require(segment.get('valid') is True and segment.get('slo_passed') is True,label+':acceptance_flags')
            require(segment.get('hard_integrity_failures')==[] and segment.get('capacity_failures')==[],label+':no_invalidated_results')
            require(segment.get('boot_id')==environment['boot_id'],label+':vm_boot')
            require(segment.get('environment_sha256')==observe.sha(self.root/'environment.json') and
                segment.get('frozen_loads_sha256')==observe.sha(self.root/'frozen-loads.json'),label+':environment_and_load_identity')
            require(segment.get('contract_sha256')==self.contract_sha and segment.get('config_sha256')==self.config_sha
                and segment.get('source_commit')==self.config['source_commit'],label+':source_and_input_identity')
            settings=frozen['loads'][provider]
            require(all(segment.get(key)==settings[key] for key in ('mode','session','concurrency','seconds','rate','case')),label+':frozen_load_configuration')
            require(segment.get('operations')==0 and not any(segment.get(key) for key in ('verify_only','empty_driver','native_heap_profile')),label+':effective_mixed_load')
            folder=self.root/'runs'/f'long-{index:02d}-{provider}-{route}'
            expected_files=(('result.json','result_sha256'),('observer.json','observer_sha256'),('persisted-state.json','persisted_state_sha256'))
            intact=all((folder/file).is_file() and observe.sha(folder/file)==segment.get(key) for file,key in expected_files)
            require(intact,label+':raw_artifact_identity')
            if not intact:continue
            raw=json.loads((folder/'result.json').read_text());observer=json.loads((folder/'observer.json').read_text())
            state=json.loads((folder/'persisted-state.json').read_text())
            require(raw.get('window_seconds',0)>=1800 and segment.get('accepted_effective_seconds')==raw.get('window_seconds')
                and raw.get('can_qualify') is True and raw.get('run_complete') is True,label+':effective_1800_seconds')
            require(raw.get('contract_sha256')==self.contract_sha and raw.get('executable_sha256')==self.config['executable_sha256'][route][provider]
                and observe.sha(self.executable(route,provider))==raw.get('executable_sha256'),label+':executed_image_identity')
            require(self.owners_retired(route,raw),label+':owner_retirement')
            counts=raw.get('counts',{})
            require(counts==segment.get('counts') and counts.get('planned',0)>0 and counts.get('not_started')==0 and counts.get('unfinished')==0
                and counts.get('planned')==counts.get('started') and counts.get('started')==counts.get('completed',0)+counts.get('failed',0),label+':request_conservation')
            failed=counts.get('failed',0)/max(1,counts.get('planned',0))
            p99=raw.get('latency_total',{}).get('offered',{}).get('p99_us_upper')
            require(p99 is not None and p99<=P99_US and failed<=ERROR_RATE,label+':recomputed_slo')
            require(state.get('valid') is True and self.state_matches(state.get('state',[]),settings['concurrency'])
                and state.get('immutable_table_counts')==self.expected_counts(),label+':persisted_state')
            require(observer.get('exit_code')==0 and observer.get('status')=='process_exited',label+':process_exit')
            for file,key in (('stdout.log','stdout_sha256'),('resources.jsonl','resource_series_sha256')):
                require((folder/file).is_file() and observe.sha(folder/file)==observer.get(key),label+':'+file)
            phases=observer.get('phases',[])
            require([p['name'] for p in phases]==['READY','START','END','DRAINED','IDLE_OPEN','IDLE_OPEN_DONE','CLOSED','IDLE','IDLE_DONE'],label+':phase_sequence')
            if len(phases)==9:require(abs((phases[2]['monotonic_ns']-phases[1]['monotonic_ns'])/1e9-raw.get('window_seconds',0))<=.25,label+':phase_timing')
            if provider=='postgresql':require(observer.get('server_window',{}).get('cgroup_user_seconds',-1)>=0
                and observer.get('server_window',{}).get('cgroup_system_seconds',-1)>=0,label+':server_cpu')
        total=sum(segment['accepted_effective_seconds'] for segment in segments)
        complete=not failures and len(segments)==8 and total>=14400
        save(self.root/'campaign-summary.json',dict(schema_version=1,complete=complete,segments=segments,
            observed_segment_count=len(segments),observed_effective_seconds=total,
            accepted_segment_count=len(segments) if complete else 0,accepted_effective_seconds=total if complete else 0,
            contract_sha256=self.contract_sha,config_sha256=self.config_sha,
            integrity_failures=failures,framework_zero_allocation_claimed=False,physical_host_exclusivity='not established'))
        if not complete:raise RuntimeError('The campaign does not have eight complete same-VM segments: '+','.join(failures))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase',choices=('prepare','smoke','calibrate','segment','summarize'))
    parser.add_argument('--config',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--index',type=int,choices=range(1,9))
    args=parser.parse_args();campaign=Campaign(args)
    if args.phase=='segment':
        if args.index is None:parser.error('--index is required for a segment')
        campaign.segment(args.index)
    else:getattr(campaign,args.phase)()


if __name__=='__main__':main()
