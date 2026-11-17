#!/usr/bin/env python3
"""One equivalent HTTP/1.1 keep-alive row; report costs, never generalize superiority."""
import http.client,json,os,pathlib,statistics,subprocess,time,urllib.parse,hashlib
R=pathlib.Path(__file__).resolve().parents[2];out=R/'artifacts/task06/B17';results=[]
managed=str(R/'tests/Server.Native/Benchmarks/bin/Release/net11.0/NativeWeave.Server.Native.Tests.dll')
for name,cmd in [('direct-native',[str(R/'target/release/examples/direct')]),('native-plus-ffi',['dotnet',managed,'native']),('kestrel',['dotnet',managed,'kestrel'])]:
 p=subprocess.Popen(cmd,cwd=R,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
 try:
  address=p.stdout.readline().strip();url=urllib.parse.urlsplit(address);assert url.port,address;c=http.client.HTTPConnection(url.hostname,url.port,timeout=5)
  def request():
   c.request('GET','/bench');r=c.getresponse();assert r.status==200 and r.read()==b'0123456789abcdef'
  for _ in range(100):request()
  durations=[];start=time.perf_counter_ns()
  for _ in range(1000):
   before=time.perf_counter_ns();request();durations.append((time.perf_counter_ns()-before)/1000)
  elapsed=(time.perf_counter_ns()-start)/1e9;c.close();status=pathlib.Path(f'/proc/{p.pid}/status').read_text();metrics={}
  for line in status.splitlines():
   if line.split(':')[0] in ('VmRSS','VmHWM','Threads'):a,b=line.split(':',1);metrics[a]=b.strip()
  p.stdin.write('\n');p.stdin.flush();stdout,stderr=p.communicate(timeout=15);assert p.returncode==0,stderr
  results.append(dict(name=name,count=1000,warmup=100,elapsed_seconds=elapsed,requests_per_second=1000/elapsed,latency_us_median=statistics.median(durations),latency_us_p95=sorted(durations)[949],process=metrics,managed_startup_and_requests=stdout.strip(),command=cmd))
 finally:
  if p.poll()is None:p.kill();p.wait()
result=dict(schema_version=1,platform='linux-x64',source_commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=R,text=True).strip(),artifact_sha256={str(f.relative_to(R)):hashlib.sha256(f.read_bytes()).hexdigest() for f in [R/'target/release/examples/direct',R/'target/release/libnativeweave_server.so',pathlib.Path(managed)]},scenario='Loopback HTTP/1.1, sequential keep-alive, GET /bench, status200, content-length16, fixed16-byte body, no TLS/compression/logging; TCP_NODELAY enabled for all server rows. Same200header/32KiBheaderbuffer/128connection bounds; no saturation.100 warmup+1000 measured. Separate client process. Direct Axum/Hyper and required Rust Host both current-thread Tokio.',limitations='Shared development environment; no CPU pinning; small row only. Native+FFI row includes C# typed dispatch and A07 operation coordination, not isolated single-call FFI latency. Managed allocation total includes startup/warmup/measurement/cleanup and excludes native allocations. RSS includes whole process; budget ledgers are application payload, not total RSS. No long-load or superiority conclusion.',results=results)
(out/'costs.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
