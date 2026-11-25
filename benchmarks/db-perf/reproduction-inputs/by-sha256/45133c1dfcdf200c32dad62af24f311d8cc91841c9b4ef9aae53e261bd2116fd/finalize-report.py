from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil

WORK = Path(__file__).resolve().parent
PLAN = json.loads((WORK / 'plan.json').read_text())
SUMMARY = json.loads((WORK / 'allocation-summary.json').read_text())
RUNS = json.loads((WORK / 'run-controller-complete.json').read_text())
BUILDS = json.loads((WORK / 'build-controller-complete.json').read_text())

def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('x') as stream:
        json.dump(value, stream, indent=2)
        stream.write('\n')

assert RUNS['status'] == 'all_eighteen_processes_validated' and len(RUNS['runs']) == 18
assert BUILDS['status'] == 'all_six_builds_validated' and len(BUILDS['builds']) == 6
assert SUMMARY['local_bytes_verified'] and SUMMARY['long_segments_credited'] == 0
assert sha(WORK / 'allocation-summary.json') == RUNS['allocation_summary_sha256']

owners = {'schema': 'DB-Perf-01.public-list-owner-summary.v1',
          'created_utc': datetime.now(timezone.utc).isoformat(),
          'korda_processes': [], 'independent_processes': [], 'postgres_servers': [],
          'scope': 'Retained lifecycle receipts from the original 18 successful processes. No database was restarted and no consumer rerun for this summary. SQL/protocol counters are cumulative diagnostics, not live owners or a claim of physical network round trips.'}
process_index = []
for item in RUNS['runs']:
    result_path = Path(item['result_path'])
    raw = json.loads(result_path.read_text())
    command_path = result_path.parent / 'probe.command.json'
    command = json.loads(command_path.read_text())
    assert command['success'] and command['exitCode'] == 0 and not command['timedOut'] and not command['observerErrors']
    assert command['clientIdentity']['pid'] == raw['ProcessId']
    assert raw['Passed'] and raw['DatabaseDisposed'] and raw['CleanupError'] is None
    assert sha(result_path) == item['result_sha256']
    identity = {'cell': item['cell'], 'round': item['round'], 'pid': raw['ProcessId'],
                'started_utc': raw['StartedUtc'], 'finished_utc': raw['FinishedUtc'],
                'result': str(result_path.relative_to(WORK)), 'result_sha256': item['result_sha256'],
                'command_receipt': str(command_path.relative_to(WORK)), 'command_receipt_sha256': sha(command_path)}
    if raw['Route'] == 'korda':
        assert len(raw['NativeOwnersAfterDrain']) == 8 and not any(raw['NativeOwnersAfterDrain'][:6])
        owners['korda_processes'].append(identity | {'six_live_owners_after_drain': raw['NativeOwnersAfterDrain'][:6],
            'cumulative_sql_and_protocol_counters': raw['NativeOwnersAfterDrain'][6:],
            'scope': raw['NativeOwnerScope']})
    else:
        assert raw['NativeOwnersAfterDrain'] is None
        owners['independent_processes'].append(identity | {'database_disposed': True, 'scope': raw['NativeOwnerScope']})
    if raw['Provider'] == 'postgresql':
        pg_path = result_path.parent / 'postgres-owner.json'
        pg = json.loads(pg_path.read_text())
        assert pg['exitCode'] == 0 and not any(k in pg for k in ['error', 'shutdownError', 'finalSampleError', 'cpuGroupError'])
        owners['postgres_servers'].append({'cell': item['cell'], 'round': item['round'],
            'receipt': str(pg_path.relative_to(WORK)), 'sha256': sha(pg_path),
            'server_identity': pg['identity'], 'exit_code': pg['exitCode'],
            'started_utc': pg['startedUtc'], 'finished_utc': pg['finishedUtc'],
            'scope': 'Observed task-owned postmaster orderly stop and successful wait4 join; raw live-tree and process-lifetime CPU observations remain in the receipt.'})
    process_index.append(identity | {'executable_sha256': raw['ExecutableSha256'],
        'primary_package_sha256': raw['PrimaryPackageSha256'], 'native_images': raw['NativeImages'],
        'build_receipt_sha256': raw['BuildReceiptSha256'], 'consumer_sources': raw['ConsumerSources'],
        'fixture_sha256': PLAN['fixture_sha256'],
        'allocation_window_counter_scope': 'Each original EndToEnd and BusinessControls window retains precise whole-process cumulative AllocatedBytes, Gen0/1/2 and Timestamp start/end values; synchronous controls also retain same-thread cumulative counters.'})
assert len(owners['korda_processes']) == 12 and len(owners['independent_processes']) == 6 and len(owners['postgres_servers']) == 9
save(WORK / 'owner-summary.json', owners)

environment_dir = WORK / 'environment'
environment_dir.mkdir()
toolchain_source = Path(PLAN['toolchain_manifest'])
assert sha(toolchain_source) == PLAN['toolchain_manifest_sha256']
shutil.copy2(toolchain_source, environment_dir / 'toolchain-manifest.json')
toolchain = json.loads(toolchain_source.read_text())
run_commands = json.loads((WORK / 'run-commands.json').read_text())
environment = {
    'schema': 'DB-Perf-01.public-list-environment.v1',
    'host': toolchain['host'], 'toolchain_manifest_sha256': PLAN['toolchain_manifest_sha256'],
    'copied_toolchain_manifest': 'environment/toolchain-manifest.json',
    'actual_runtime': '.NET 11.0.0', 'sdk': '11.0.100', 'clang': '23.1.5', 'postgres_tools': '18.0',
    'build_interval_utc': [BUILDS['started_utc'], BUILDS['finished_utc']],
    'consumption_interval_utc': [RUNS['started_utc'], RUNS['finished_utc']],
    'build_processor_count': 2, 'consumption_DOTNET_PROCESSOR_COUNT': run_commands['DOTNET_PROCESSOR_COUNT'],
    'actual_consumer_affinity': run_commands['affinity'],
    'build_overlap_with_consumption': False,
    'scope': 'New Debian container allocation diagnostic; no equivalence to the dedicated Ubuntu formal performance host is claimed. Original build and observer commands, raw resource samples and cleanup records are retained.',
}
assert environment['consumption_DOTNET_PROCESSOR_COUNT'] is None
assert BUILDS['finished_utc'] < RUNS['started_utc']
save(environment_dir / 'identity.json', environment)

cell_index = []
derived = []
for cell in SUMMARY['cells']:
    records = [json.loads(Path(r['path']).read_text()) for r in cell['raw_results']]
    per_process = [sum(w['RawAllocatedBytes'] for w in r['EndToEnd']) / sum(w['Succeeded'] for w in r['EndToEnd']) for r in records]
    all_windows = [w for r in records for w in r['EndToEnd']]
    control_gc = [sum(w[f'Gen{g}Collections'] for r in records for w in r['BusinessControls']) for g in range(3)]
    record = {k: cell[k] for k in ['label', 'provider', 'route', 'processes', 'source_label',
              'primary_package_sha256', 'executable_sha256', 'graph', 'end_to_end',
              'isolated_business_graph_control', 'unresolved_remainder_estimate_bytes_per_query',
              'exact_framework_bytes', 'framework_zero_claim']}
    record.update(process_end_to_end_bytes_per_query=per_process,
                  window_bytes_per_query_min=min(w['RawAllocatedBytes']/w['Succeeded'] for w in all_windows),
                  window_bytes_per_query_max=max(w['RawAllocatedBytes']/w['Succeeded'] for w in all_windows),
                  observed_incomplete_returns=sum(w['ObservedIncompleteReturns'] for w in all_windows),
                  cross_thread_awaits=sum(w['CrossThreadAwaits'] for w in all_windows),
                  clone_interval_process_gc_counts=control_gc)
    derived.append(record)
for entry in PLAN['cells']:
    app = Path(entry['app'])
    path = app / 'build-receipt.json'
    build = json.loads(path.read_text())
    cell_index.append({
        'cell': entry['id'], 'app': str(app.relative_to(WORK)), 'build_receipt_sha256': sha(path),
        'executable': str((app / 'out/BusinessResults').relative_to(WORK)),
        'executable_sha256': build['executable_sha256'],
        'executable_bytes': (app / 'out/BusinessResults').stat().st_size,
        'native_images': build['native_images'], 'consumer_sources': build['sources'],
        'actual_restored_archives': build['packages'], 'project_reference_count': build['project_references'],
        'verification': 'Source/archive/ELF/native bytes were verified by the successful original builder and original analyze.py --verify-local after all 18 runs. This index does not rebuild or rerun them.',
    })
comparisons = []
for provider in ['sqlite', 'postgresql']:
    rows = {c['label']: c for c in derived if c['provider'] == provider}
    before, after, independent = [rows[k]['end_to_end']['bytes_per_operation'] for k in ['before', 'after', 'independent']]
    comparisons.append({'provider': provider, 'before_bytes_per_query': before, 'after_bytes_per_query': after,
        'independent_bytes_per_query': independent, 'after_minus_before_bytes': after-before,
        'after_change_percent': 100*(after/before-1), 'after_to_independent_ratio': after/independent,
        'same_retained_business_graph_control_bytes': 7480.0,
        'exact_framework_bytes': None, 'scope': 'Actual measured end-to-end allocation change, independent isolated graph control; no formal timing acceptance.'})
save(WORK / 'attribution-detail.json', {'schema': 'DB-Perf-01.public-list-attribution.v1',
     'summary_sha256': sha(WORK / 'allocation-summary.json'), 'cells': derived, 'comparisons': comparisons,
     'no_category_owned_gc_events': True, 'business_control_is_exact_in_query_allocation_trace': False})

labels = {'before':'优化前 Korda','after':'当前 Korda','independent':'Dapper.AOT'}
lines = [
    '# DB-Perf-01：当前候选的真实 100 行窄 DTO 分配诊断', '',
    '本轮在新的 Debian 13.7 / Linux x64 环境完成 **6 个源码树外的普通 PackageReference Native AOT app、18 个独立消费进程**。三条托管路线返回相同的实际 `List<BusinessRow>`，每次包含 100 个独立 DTO、80 个独立且未驻留的字符串及 20 个 NULL。`BusinessRow` 为 `Id` / `Note` 窄 DTO，本报告不把它称为完整订单宽 DTO。', '',
    '**结果：业务返回图的隔离构造对照在全部六格均为 7,480 B/图，保持不变。Korda 端到端总分配在两库均从约 69,436 B/查询降至约 21,205–21,222 B/查询，减少约 69.44%。精确框架额外分配仍未由此诊断单独证明；所有精确框架字段保留为 null，未宣称框架 0 B。**', '',
    '## 绑定与测量范围', '',
    f'- 优化前：固定 `main` 提交 `771bc77cbd68d3f932b99ed2497c0697bbba5f02`，两库 `0.1.0-preview.3` 原始候选包。',
    f'- 当前：生产与消费源码固定 `{PLAN["consumer_ref"]}`；实际包生产提交 `{PLAN["package_producer_commit"]}` 的树与该提交相同，版本 `0.1.0-ci.1`。这属于候选包公开 API 消费，不替代合并后真实 NuGet 发布包读回验证。',
    '- 独立 ORM：Dapper 2.1.100、Dapper.AOT 1.1.0；PostgreSQL 使用 Npgsql 11.0.0，SQLite 使用 Microsoft.Data.Sqlite.Core 11.0.0 / SQLitePCLRaw.provider.sqlite3 3.0.5。',
    f'- 构建区间：`{BUILDS["started_utc"]}` 至 `{BUILDS["finished_utc"]}`。SDK 11.0.100、Clang 23.1.5，严格串行，编译期间 `DOTNET_PROCESSOR_COUNT=2`。',
    f'- 消费区间：`{RUNS["started_utc"]}` 至 `{RUNS["finished_utc"]}`。运行时明确移除 `DOTNET_PROCESSOR_COUNT`，实际可用 CPU affinity 为 `{run_commands["affinity"]}`，消费期间没有构建。',
    '- 每格 3 个独立进程，每进程暖机 64 个完整查询，测量 3 × 128 次查询；数据库排空后再测 3 × 1,024 次业务图构造对照。总计 6,912 次计量查询、691,200 行及 55,296 个隔离业务图。交错顺序在启动前冻结。',
    '- 端到端边界包括动态查询构造、绑定、全部物化、逐字段及 digest 消费和查询/游标归还；session/source 建立及最终归还在此区间外。公共异步 harness 与运行时分配保留在总量中。',
    '- 这是新环境的分配与正确性诊断，**不提供正式吞吐、延迟、CPU 或内存性能验收，不计入任何长测段**。原观察器的 CPU/内存时间序列仍保留供审查；其进程生命周期总量包含初始化、暖机、查询和 clone，不能混作查询窗口的性能值。', '',
    '## 端到端总分配', '',
    '每行均汇总 3 个进程的 9 个原始查询窗口：1,152 次成功查询、115,200 行。累计原始字节及各代 GC 起止值保留在各 `result.json`，以下数值按原始计数计算。', '',
    '| 数据库 | 路线 | 原始总分配 B | B/查询 | B/行 | 查询窗口内进程 GC (0/1/2) |',
    '| --- | --- | ---: | ---: | ---: | --- |',
]
for provider in ['sqlite','postgresql']:
    for label in ['before','after','independent']:
        c = next(c for c in derived if c['provider']==provider and c['label']==label)
        e=c['end_to_end']
        lines.append(f"| {provider} | {labels[label]} | {e['raw_allocated_bytes']:,} | {e['bytes_per_operation']:,.3f} | {e['bytes_per_row']:,.5f} | {' / '.join(map(str,e['gc_counts']))} |")
lines += ['', '| 数据库 | 当前相对优化前 B/查询 | 变化 | 当前 / Dapper.AOT 总分配 |',
          '| --- | ---: | ---: | ---: |']
for c in comparisons:
    lines.append(f"| {c['provider']} | {c['after_minus_before_bytes']:,.3f} | {c['after_change_percent']:.3f}% | {c['after_to_independent_ratio']:.5f}× |")
lines += ['', '当前 PostgreSQL 三个进程分别为 21,185.063 / 21,219.917 / 21,210.958 B/查询；当前 SQLite 为 21,242.438 / 21,219.833 / 21,203.396 B/查询。这些重复过程的分配差异远小于观察到的约 48.2 KB/查询下降，但不能据此作正式性能时序或饱和点判断。', '',
    '## 业务结果与框架归因', '',
    '所有真实结果和 clone 的已保留图形相同：List 容量 128、100 个 DTO、80 个独立字符串、20 个 NULL、668 个 UTF-16 字符，固定结果 digest 为 `12366612025187139697`。clone 在数据库和 provider 池完成归还之后创建新的列表、DTO 和字符串；每次都完整消费。', '',
    '全部 **54 个 clone 窗口**的当前线程分配都严格为 **7,480 B/图**，每格合计 9,216 图、68,935,680 B。clone 窗口另保留全进程原始计数，它可能包含其他线程的运行时活动。这证明的是相同已保留业务图的独立构造成本；它不是实际数据库查询内部的逐栈分配追踪。', '',
    '| 数据库 | 路线 | 业务图隔离对照 B/图 | 总量减对照的未归因余量 B/查询 | 精确框架分配 |',
    '| --- | --- | ---: | ---: | --- |']
for provider in ['sqlite','postgresql']:
    for label in ['before','after','independent']:
        c=next(c for c in derived if c['provider']==provider and c['label']==label)
        lines.append(f"| {provider} | {labels[label]} | 7,480 | {c['unresolved_remainder_estimate_bytes_per_query']:,.3f} | 未证明（null） |")
lines += ['', '未归因余量包含框架、运行时、harness，以及对照与真实执行的潜在模型差异。例如列表扩容时被丢弃的数组不在最终保留图中，因此不在这个 clone 对照内。不得把该差值命名为精确框架额外分配，也不得将业务图需要的 7,480 B 视作框架未达到 0 B 的缺陷。', '',
    'GC 是整个托管堆的收集事件，不能按对象来源拆成“业务 GC 次数”和“框架 GC 次数”。表中的查询窗口 GC 与 `attribution-detail.json` 中 clone 窗口 GC 都只说明事件出现在哪个计量区间；clone 期间的收集也可能处理此前产生的对象。', '',
    '所有 Korda 查询窗口共 1,152 次/格都观察到返回时尚未完成的 Task；跨线程 await 也保留在原始记录中。Dapper PostgreSQL 同样有异步未完成返回；Dapper SQLite 的该路径没有未完成返回或跨线程 await。总分配计量采用精确全进程累计计数，覆盖这些异步线程。', '',
    '## 正确性、数据库与所有权', '',
    '- 18/18 原始进程、54/54 查询窗口、54/54 clone 窗口全部通过；无查询失败、无重试、无超时、无观察器错误。每格均有三个不同的 PID/UTC 起点/ELF 身份组合。',
    '- 12 个 Korda 进程排空后，source/session/operation/cursor/buffer/handle 六个原生 owner 全部为 0。末尾 SQL/protocol 项是累计计数，不是活跃 owner，也没有被解释为物理网络往返。',
    '- 6 个 Dapper 进程的应用连接已 disposed，provider 池已清除；provider 内部原生 owner 未插桩，保留为未观测。',
    '- 9 个独立 task-owned PostgreSQL 18 集群均正常退出并完成观察器 join，退出码全为 0。UTF8、fsync=on、synchronous_commit=on、full_page_writes=on、read committed 等实际设置逐进程保留。',
    '- 每进程使用新的 SQLite 文件或 PostgreSQL 集群。旧 runtime 的 35-case initializer 与本 probe 的表、数据量和结果契约不同，因此没有复用；只复用了已验证归档内的固定 SQLite 共享引擎。', '',
    '## 复现和恢复索引', '',
    f'- `plan.json`：输入、启动前冻结顺序与归因边界；SHA256 `{sha(WORK / "plan.json")}`。',
    f'- `inputs/fixture.json`：完整 100 行语义数据；SHA256 `{PLAN["fixture_sha256"]}`。',
    f'- `allocation-summary.json`：原仓库分析器的六格汇总；SHA256 `{sha(WORK / "allocation-summary.json")}`。',
    '- `build-commands.json` / `run-commands.json` / `analysis-command.json`：实际命令、具体 PackageReference feed/version、运行目录和固定 observer 提交。新的复现必须使用新的输出目录，不能覆盖已有尝试。',
    '- `apps/<cell>/build-receipt.json` / `project.assets.json` / `generated` / `out`：同一消费源码、实际依赖包、生成映射、6 个 ELF 及原生映像。原始成功文件保留供后续经授权的小范围对照复用。',
    '- `evidence/<cell>/round-<n>/process-0/`：原始累计计数、命令、资源时间序列、数据库设置与停止记录。',
    '- `attribution-detail.json` / `owner-summary.json` / `environment/identity.json` / `evidence-index.json`：分配范围、生命周期和身份交叉索引。',
    '- `environment/toolchain-manifest.json`：SDK、Clang、PostgreSQL 和系统共享库精确版本/哈希，原始工具链 manifest 的副本。', '',
    '本次只恢复和验证证据，没有更改产品、公开 API 或仓库基准实现，也没有修改 Plan、提交、推送或触发发布。', '',
]
with (WORK / 'report-zh.md').open('x') as f: f.write('\n'.join(lines))

metadata_files = []
for pattern in ['*.json', '*.md', 'logs/*', 'frozen-source/**/*', 'environment/*', 'evidence/**/*']:
    for path in WORK.glob(pattern):
        if path.is_file(): metadata_files.append(path)
index = {
    'schema': 'DB-Perf-01.public-list-evidence-index.v1',
    'created_utc': datetime.now(timezone.utc).isoformat(), 'root': str(WORK),
    'scope': 'Original six successful ELF builds and eighteen successful Native AOT allocation processes, new Debian diagnostic only. No reruns or recompilation were performed during finalization.',
    'summary_sha256': sha(WORK / 'allocation-summary.json'), 'fixture_sha256': PLAN['fixture_sha256'],
    'source_commit': PLAN['consumer_ref'], 'package_producer_commit': PLAN['package_producer_commit'],
    'cells': cell_index, 'processes': process_index,
    'metadata_files': {str(p.relative_to(WORK)): {'sha256': sha(p), 'bytes': p.stat().st_size}
                       for p in sorted(set(metadata_files))},
    'preservation': 'All apps, candidate nupkg inputs, original ELF/native images, restored package archives, generated sources, fresh database directories, raw process records and logs remain in place. No compression or large binary rehash was run during another agent\'s exclusive micro-measurement window.',
}
save(WORK / 'evidence-index.json', index)
print(json.dumps({'report': str(WORK / 'report-zh.md'), 'report_sha256': sha(WORK / 'report-zh.md'),
                  'index': str(WORK / 'evidence-index.json'), 'index_sha256': sha(WORK / 'evidence-index.json'),
                  'comparisons': comparisons, 'korda_owners_zero': 12,
                  'postgres_servers_joined_successfully': 9}))
