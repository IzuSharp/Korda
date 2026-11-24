#!/usr/bin/env python3
"""Observe one real process without adding work to its managed allocation total."""
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import selectors
import signal
import subprocess
import sys
import time


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def process(pid):
    """CPU counts are cumulative clock ticks, RSS/PSS are instantaneous bytes."""
    root=Path('/proc')/str(pid)
    try:
        stat=(root/'stat').read_text()
        fields=stat[stat.rindex(')')+2:].split()
        value=dict(pid=pid,ppid=int(fields[1]),start_ticks=int(fields[19]),
                   user_ticks=int(fields[11]),system_ticks=int(fields[12]),
                   minor_faults=int(fields[7]),major_faults=int(fields[9]),threads=int(fields[17]))
        status={line.split(':',1)[0]:line.split(':',1)[1].strip() for line in (root/'status').read_text().splitlines() if ':' in line}
        value['rss_bytes']=int(status.get('VmRSS','0 kB').split()[0])*1024
        value['peak_rss_bytes']=int(status.get('VmHWM','0 kB').split()[0])*1024
        value['affinity']=status.get('Cpus_allowed_list')
        try:
            smaps={line.split(':',1)[0]:line.split(':',1)[1].strip() for line in (root/'smaps_rollup').read_text().splitlines() if ':' in line}
            value['pss_bytes']=int(smaps['Pss'].split()[0])*1024
            value['private_bytes']=sum(int(smaps.get(key,'0 kB').split()[0])*1024 for key in ('Private_Clean','Private_Dirty','Private_Hugetlb'))
        except (OSError,KeyError,ValueError):
            value['pss_bytes']=None;value['private_bytes']=None
        return value
    except (OSError,ValueError):
        return None


def descendants(pid):
    found=[];pending=[pid]
    while pending:
        current=pending.pop()
        if current in found:continue
        found.append(current)
        try:
            pending.extend(int(x) for x in Path(f'/proc/{current}/task/{current}/children').read_text().split())
        except OSError:
            pass
    return found


def system_cpu():
    return {fields[0]:list(map(int,fields[1:])) for line in Path('/proc/stat').read_text().splitlines()
            if (fields:=line.split()) and fields[0].startswith('cpu')}


def cgroup_cpu(path):
    if path is None:return None
    values={key:int(value) for key,value in (line.split() for line in (Path(path)/'cpu.stat').read_text().splitlines())}
    if not {'usage_usec','user_usec','system_usec'}.issubset(values):
        raise RuntimeError('PostgreSQL cgroup does not expose complete CPU accounting')
    return values


class ServerCounter:
    def __init__(self,pid):
        self.pid=pid;self.last={};self.disappeared=set()

    def capture(self):
        live=[];identities=set()
        for pid in descendants(self.pid) if self.pid else []:
            item=process(pid)
            if item is None:continue
            key=(pid,item['start_ticks']);identities.add(key);live.append(item);self.last[key]=item
        self.disappeared.update(set(self.last)-identities)
        return dict(processes=live,
            cumulative_user_ticks=sum(item['user_ticks'] for item in self.last.values()),
            cumulative_system_ticks=sum(item['system_ticks'] for item in self.last.values()),
            rss_bytes=sum(item['rss_bytes'] for item in live),
            pss_bytes=sum(item['pss_bytes'] for item in live) if all(item['pss_bytes'] is not None for item in live) else None,
            disappeared_identities=[list(item) for item in sorted(self.disappeared)])


def run(command,folder,seconds,*,server_pid=None,server_cgroup=None,env=None,timeout_setup=300,operations=0):
    """A fresh receipt folder is required; failed attempts are never overwritten."""
    folder=Path(folder);folder.mkdir(parents=True,exist_ok=False)
    command=list(map(str,command));clock_ticks=os.sysconf('SC_CLK_TCK')
    receipt=dict(schema_version=1,command=command,started_utc=utc(),clock_ticks_per_second=clock_ticks,
        client_scope='one process, including all its threads; in-process receipt is authoritative for exact CPU boundaries',
        server_scope='Task-owned cgroup v2 CPU totals include exited/reaped PostgreSQL backends. Per-process snapshots and last-observed family sums remain separate diagnostics; their disappearance can lose terminal CPU.',
        server_cgroup=str(server_cgroup) if server_cgroup else None,
        watchdog=dict(setup_or_finite_probe_seconds=timeout_setup,timed_drain_seconds=30,timed_cleanup_seconds=35,terminate_grace_seconds=10),
        memory_scope='1 Hz external RSS/PSS snapshots plus all phase boundaries; maxima are sampled peaks, distinct from ru_maxrss lifetime peak',
        physical_host_exclusivity='not established',phases=[],status='running')
    (folder/'observer.json').write_text(json.dumps(receipt,indent=2)+'\n')
    server=ServerCounter(server_pid);started=time.monotonic();last_sample=0.;active_start=None
    phase='SETUP';snapshots=[];phase_samples={};watchdog=False;child=None;selector=None
    try:
        with (folder/'stdout.log').open('wb') as log,(folder/'resources.jsonl').open('w') as series:
            child=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,env=env,start_new_session=True,bufsize=0)
            receipt['client_pid']=child.pid
            selector=selectors.DefaultSelector();selector.register(child.stdout,selectors.EVENT_READ)
            partial=b''
            def sample(reason):
                item=dict(monotonic_ns=time.monotonic_ns(),utc=utc(),phase=phase,reason=reason,
                          client=process(child.pid),server=server.capture(),server_cgroup_cpu=cgroup_cpu(server_cgroup),system_cpu_ticks=system_cpu())
                series.write(json.dumps(item,separators=(',',':'))+'\n');series.flush();snapshots.append(item)
                return item
            sample('process-start')
            while True:
                now=time.monotonic()
                if now-last_sample>=1:
                    sample('periodic');last_sample=now
                # Finite-count probes have no active --seconds budget. They remain
                # bounded by the independent setup/probe watchdog, never relabeled
                # as a timed interval. Timed runs allow 30 s drain plus 35 s cleanup.
                limit=started+timeout_setup if active_start is None or operations else active_start+seconds+65
                if now>limit and child.poll() is None:
                    watchdog=True;sample('watchdog-before-terminate')
                    os.killpg(child.pid,signal.SIGTERM)
                    try:child.wait(timeout=10)
                    except subprocess.TimeoutExpired:os.killpg(child.pid,signal.SIGKILL)
                    receipt['watchdog_reason']='No START within setup budget' if active_start is None else 'Measurement, drain and owner-retirement budget exceeded'
                events=selector.select(.2)
                for key,_ in events:
                    data=os.read(key.fileobj.fileno(),65536)
                    if not data:selector.unregister(key.fileobj);continue
                    log.write(data);log.flush();partial+=data
                    while b'\n' in partial:
                        line,partial=partial.split(b'\n',1)
                        if line.startswith(b'DB_PERF_PHASE '):
                            name=line.decode(errors='replace').split()[1]
                            phase=name
                            if name=='START':active_start=time.monotonic()
                            stamp=sample('phase-boundary');phase_samples[name]=stamp
                            receipt['phases'].append(dict(name=name,utc=stamp['utc'],monotonic_ns=stamp['monotonic_ns']))
                            print(folder.name,name,stamp['utc'],flush=True)
                if child.poll() is not None and not selector.get_map():break
            child.wait();selector.close();sample('process-exit')
    except BaseException as error:
        receipt.update(status='observer_failure',observer_error=repr(error),finished_utc=utc(),elapsed_seconds=time.monotonic()-started)
        # The sampler owns a dedicated child process group. Losing accounting
        # must retire that workload before this failed attempt can return.
        try:
            if child is not None:
                if child.poll() is None:
                    try:os.killpg(child.pid,signal.SIGTERM)
                    except ProcessLookupError:pass
                    try:child.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        try:os.killpg(child.pid,signal.SIGKILL)
                        except ProcessLookupError:pass
                        child.wait(timeout=10)
                receipt['exit_code']=child.returncode
                receipt['child_reaped']=child.poll() is not None
        except BaseException as cleanup:
            receipt['observer_cleanup_error']=repr(cleanup)
        finally:
            if selector is not None:selector.close()
            if child is not None and child.stdout is not None:child.stdout.close()
        try:(folder/'observer.json').write_text(json.dumps(receipt,indent=2)+'\n')
        except OSError as recording:
            print(json.dumps(dict(observer_failure=repr(error),recording_failure=repr(recording),child_reaped=receipt.get('child_reaped'))),file=sys.stderr,flush=True)
        raise
    finally:
        if selector is not None:selector.close()
        if child is not None and child.stdout is not None:child.stdout.close()
    active=[item for item in snapshots if item['phase']=='START']
    def peak(scope,field):
        values=[item[scope].get(field) for item in active if item[scope] is not None and item[scope].get(field) is not None]
        return max(values) if values else None
    receipt.update(status='watchdog_failure' if watchdog else 'process_exited',exit_code=child.returncode,
        finished_utc=utc(),elapsed_seconds=time.monotonic()-started,samples=len(snapshots),active_samples=len(active),
        client_sampled_peak_rss_bytes=peak('client','rss_bytes'),client_sampled_peak_pss_bytes=peak('client','pss_bytes'),
        server_sampled_peak_rss_bytes=peak('server','rss_bytes'),server_sampled_peak_pss_bytes=peak('server','pss_bytes'),
        stdout_sha256=sha(folder/'stdout.log'),resource_series_sha256=sha(folder/'resources.jsonl'))
    for prefix,start_name,end_name in [('window','START','END'),('drain','END','DRAINED'),('idle_open','IDLE_OPEN','IDLE_OPEN_DONE'),('idle_closed','IDLE','IDLE_DONE')]:
        if start_name not in phase_samples or end_name not in phase_samples:continue
        first,last=phase_samples[start_name],phase_samples[end_name]
        begin,end=first['server'],last['server']
        receipt['server_'+prefix]=dict(
            user_seconds=(end['cumulative_user_ticks']-begin['cumulative_user_ticks'])/clock_ticks,
            system_seconds=(end['cumulative_system_ticks']-begin['cumulative_system_ticks'])/clock_ticks,
            interval_seconds=(last['monotonic_ns']-first['monotonic_ns'])/1e9,
            disappeared_during_interval=[item for item in end['disappeared_identities'] if item not in begin['disappeared_identities']],
            boundary_scope='external phase observations; includes stdout/observer notification delay')
        if first['server_cgroup_cpu'] is not None and last['server_cgroup_cpu'] is not None:
            row=receipt['server_'+prefix]
            row['family_last_observed_user_seconds']=row['user_seconds']
            row['family_last_observed_system_seconds']=row['system_seconds']
            row['user_seconds']=row['cgroup_user_seconds']=(last['server_cgroup_cpu']['user_usec']-first['server_cgroup_cpu']['user_usec'])/1e6
            row['system_seconds']=row['cgroup_system_seconds']=(last['server_cgroup_cpu']['system_usec']-first['server_cgroup_cpu']['system_usec'])/1e6
            receipt['server_'+prefix]['authoritative_cpu']='cgroup v2, including exited and reaped descendants; externally observed phase boundaries'
    (folder/'observer.json').write_text(json.dumps(receipt,indent=2)+'\n')
    return receipt
