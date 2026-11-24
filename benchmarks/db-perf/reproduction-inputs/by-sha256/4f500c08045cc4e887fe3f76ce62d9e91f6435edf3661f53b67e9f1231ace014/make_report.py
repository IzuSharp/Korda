#!/usr/bin/env python3
"""Render the frozen comparison without replacing any raw observation."""
import hashlib
import json
from pathlib import Path

BASE = Path(__file__).resolve().parent
ROOT = BASE.parents[1]
read = lambda p: json.loads(Path(p).read_text())
sha = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()
c = read(BASE / 'comparison.json')
v = read(BASE / 'validation-index.json')
b = read(BASE / 'before-reuse.json')
build = read(BASE / 'host-build-v1/build-receipt.json')
out = []
add = out.append
add('# DB-Perf-01 · 第 15 项：私有 Move 异步状态机复用\n')
add('本项只改变 `TypedStream` 私有 `MoveAsync` 的 builder，并直接返回其 `ValueTask<bool>`。公开签名、移动互斥、取消、映射读作用域、错误和清理顺序保持原样。完整查询与补批仍有框架及运行时分配；批内同步数值映射的局部 0 B 不代表整个数据库框架达到 0 GC。\n')
add('这是源码项目实际 Native AOT 的定位对照。所有被测调用使用正常公开 Fluent API，但测试程序集通过源码项目引用构建，因此不替代最终候选的外树 PackageReference 验收。这里只报告分配及正确性，不从本机 elapsed ticks 推导正式 CPU、吞吐或尾延迟收益。\n')
add('## 输入与复用边界\n')
add(f'- 优化前直接引用第 14 项 **after** 六个进程的原始记录，原 phase、文件、命令、来源与 SHA 均未重标。原 ELF 为 `{b["originalExecutableSha256"]}`；原构建 HEAD `{b["originalBuildHead"]}`，sourceDirty=true。恢复引用清单 `before-reuse.json` SHA `{sha(BASE / "before-reuse.json")}`。')
add(f'- 优化后 ELF `{build["executableSha256"]}`；实际构建 HEAD `{build["gitHead"]}`，sourceDirty=true；329 输入 source manifest `{build["sourceManifestSha256"]}`。与前一输入清单相比仅 `TypedStream.cs` 与新增消费语义测试 `AllocationProbeRaces.cs` 两文件改变。')
add(f'- 已验证 Data DLL 保持 `{v["frozenDataDllSha256"]}`，以 `BuildProjectReferences=false` 顺序构建受影响模块与测试。各 build receipt 记录完整引用 DLL 的前后 SHA，未覆盖既有 DLL 的真实生产者元数据；此项未改变 Data 行为，不重跑无关 Data gate。')
add('- 公开 `FetchProbe.cs` 精确保持 `2d7e50e095d630a0caf22a773bbd94c869c60cb50127175a8d050d51f5417d6c`。同 controller、CPU affinity、查询/投影、原生库、数据库、暖机与计量参数。外层 runner 仅改本项标签并显式复用第 14 项 SQLite 文件路径；两个 runner 的真实 SHA 分别保留，不声称 runner 字节相同。')
add('- 两库各 3 个独立进程，每种完整查询 64 次暖机、9 窗 × 128 查询；每库每版本每种形状合计 3,456 查询，256 行形状合计 884,736 行。无并行构建或其他本地数据库计量。\n')
for provider, p in c['providers'].items():
    add(f'- {provider} 实际加载原生库 SHA `{p["loadedNativeSha256"]}`，前后相同。')
add('\n## 完整查询的原始总字节先于除法\n')
add('边界从公开 `AsAsyncEnumerable`/枚举器构造开始，包含全部结果消费、最后一次 false 和 Dispose。输出为 Int64，无必须新建的 DTO、字符串、列表或结果数组。全进程 `GC.GetTotalAllocatedBytes(true)` 覆盖工作线程；运行时与测量器成本保留在原数中，不减去空控。\n')
add('| 数据库 | 结果行/查询 | before 累计 B | after 累计 B | before B/查询 | after B/查询 | Δ B/查询 | 分配变化 |')
add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
for provider, p in c['providers'].items():
    for rows, q in p['fullQueriesByResultRows'].items():
        a, z = q['before'], q['after']
        add(f'| {provider} | {rows} | {a["rawAllocatedBytes"]:,} | {z["rawAllocatedBytes"]:,} | {a["bytesPerOperation"]:.9f} | {z["bytesPerOperation"]:.9f} | {q["deltaBytesPerOperation"]:.9f} | {q["percentChange"]:.6f}% |')
add('\n| 数据库 | 256 行 before B/行 | 256 行 after B/行 | 256 行 Gen0 before→after | Gen1/Gen2 before→after |')
add('| --- | ---: | ---: | --- | --- |')
for provider, p in c['providers'].items():
    q = p['fullQueriesByResultRows']['256']; a, z = q['before'], q['after']
    add(f'| {provider} | {a["bytesPerRow"]:.12f} | {z["bytesPerRow"]:.12f} | {a["gen0"]}→{z["gen0"]} | {a["gen1"]}/{a["gen2"]}→{z["gen1"]}/{z["gen2"]} |')
add('\n这些是全部窗口累计值，未丢弃尾部或池未命中窗口。每窗原始 before/after 累计字节、GC 代次数、计时、操作/行数、退休计数和 native 六所有者计数留在六份 `fetch-allocation.json`；汇总同时保留字节分布。\n')
add('## 补批、批内读取与控制\n')
add('补批片段从旧批最后一行后开始，包含旧批归还、一次真实 fetch、验证/租约、新批第一行（row33）；每库每版本 384 次，均独立观察到一次退休。它不包含整次查询创建与最终关闭，因此不能代替上表。\n')
add('| 数据库 | before 累计 B / 384 批 | after 累计 B / 384 批 | before B/批 | after B/批 | Δ B/批 |')
add('| --- | ---: | ---: | ---: | ---: | ---: |')
for provider, p in c['providers'].items():
    q = p['slices']['supplemental-fetch-and-first-row']; a, z = q['before'], q['after']
    add(f'| {provider} | {a["rawAllocatedBytes"]:,} | {z["rawAllocatedBytes"]:,} | {a["bytesPerOperation"]:.9f} | {z["bytesPerOperation"]:.9f} | {q["deltaBytesPerOperation"]:.9f} |')
for provider, p in c['providers'].items():
    q = p['slices']['supplemental-fetch-and-first-row']
    add(f'\n{provider} 补批原始单窗字节分布：before `{json.dumps(q["before"]["rawBytesPerWindowDistribution"], sort_keys=True)}`；after `{json.dumps(q["after"]["rawBytesPerWindowDistribution"], sort_keys=True)}`。')
add('\n批内片段每库每版本读取 11,904 行、384 窗。')
for provider, p in c['providers'].items():
    q = p['slices']['within-first-batch']
    add(f'- {provider} 批内累计 B：{q["before"]["rawAllocatedBytes"]}→{q["after"]["rawAllocatedBytes"]}；每窗实际退休与 incomplete 均为 0。')
    for name, control in p['controls'].items():
        add(f'- {provider} 控制 `{name}`：before {control["before"]["rawAllocatedBytes"]} B、after {control["after"]["rawAllocatedBytes"]} B；{control["after"]["operations"]} 次，各自保留，不从业务窗口扣除。')
add('\n`IncompleteMoves` 只是调用后一次观察尚未成功完成，不等于精确 await 挂起次数；`MoveThreadChanges` 才提供跨线程续执行的独立证据。两字段均逐窗保留。\n')
add('| 数据库 | 256 行 incomplete before→after | 256 行 thread changes before→after | 补批 thread changes before→after |')
add('| --- | ---: | ---: | ---: |')
for provider, p in c['providers'].items():
    q=p['fullQueriesByResultRows']['256']; s=p['slices']['supplemental-fetch-and-first-row']
    add(f'| {provider} | {q["before"]["incompleteMovesObserved"]}→{q["after"]["incompleteMovesObserved"]} | {q["before"]["moveThreadChanges"]}→{q["after"]["moveThreadChanges"]} | {s["before"]["moveThreadChanges"]}→{s["after"]["moveThreadChanges"]} |')
add('\n## 正确性与所有权\n')
add('- Streaming 四个 fresh gate 均成功：16 个批内严格零窗口、65,520 行 raw 0 B；owned batch 405 assertions；stable-owner；118 个移动/清理竞态及新增消费语义 assertions，19 个 native 借用生命期 assertions。')
add('- 完整真实两库 Streaming suite：615 assertions、60 cases，Native AOT，managed-live-bytes=0、native-wrappers=0。')
add(f'- 完整 Host 两库 suite：本次实际 {v["host"]["actualAssertions"]} assertions/{v["host"]["actualManagedRetirements"]} managed retirements；5,942 个 parser oracle 输入；12 个真实 mapper barrier 场景（两 provider × Dispose/Cancel/SourceClose × 成功/异常），各 provider 107 assertions。所有最终 source/session/operation/cursor/buffer/handle 为 0。')
add('- 两种批次存储下，延后 GetResult 的旧 ValueTask 不受下一次 pending move 覆盖；仅一次 AsTask 转换产生的 Task 在后续 move 后仍能重复 await。测试未承诺重复消费原始 ValueTask。')
add('- 固定 FetchProbe 的完整查询即时暖态六所有者快照前后一致，进程排空最终六计数为 0。0/1/256 行分别实际观察 3/4/11 个 operation retirements；每查询 1 次 SQL，PostgreSQL 每查询 5 个协议提交、SQLite 0 个协议提交。fetch 1/2/9 次仅由固定批大小与 EOS 契约推算，分别标注；协议提交不等同物理网络往返。')
add('- RowBatchLease 依然是前项的 80 B/批，本项三窗各 64 批实测 5,120 B，未把第 14 项 copy 节省再计一次。Native buffer 所有权与同步 SafeHandle scope 未修改。\n')
add('## 池保留、未证明范围与停止依据\n')
add('独立 source review 绑定实际 NativeAOT CoreLib 到 `dotnet/dotnet@297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f`，确认归池发生在对应 GetResult 的 finally；尚未消费的 pending 或 completed ValueTask 持有自己的 box。每个封闭状态机类型有参与归还的存活线程各一个 TLS slot，加 Environment.ProcessorCount 个共享 slot。这是随参与线程与类型变化的运行时缓存，不是每个连接或每个流固定一个池。\n')
add('本轮固定 warm64 探针没有独立量化首次初始化、容量未命中、线程增长、未消费结果持有及空闲托管保留量。窗口内发生的任何开销仍计入原始总字节，但不能据此把未命中路径标为 0 B，也不能以源码的 slot 界限代替 RSS/PSS 或 retained managed heap 实测。此缺口须在后续池诊断/最终公开包清单保留。\n')
add('本项的保留依据是同一公开探针的完整查询与补批分配差异，以及新增消费语义和既有生命期正确性通过。它不构成整个优化批次的停止依据：内部提交、完成和归还仍有正数分配；更深的内部完成链候选另行评估，不把未尝试的状态复用一概称为语义上不可能。\n')
add('## 证据与复现入口\n')
add(f'- `comparison.json` SHA `{sha(BASE / "comparison.json")}`；`validation-index.json` SHA `{sha(BASE / "validation-index.json")}`；`after-summary.json` SHA `{sha(BASE / "after-summary.json")}`。')
add(f'- before 直接引用 `../native-batch-borrow-v1/after-summary.json` SHA `{c["beforeSummarySha256"]}`；原文件名和 phase 未变。')
add('- 所有源码 manifest/tar、依赖 DLL SHA、恢复图、实际构建 argv、退出码与完整日志在 `modules-build-v1`、`streaming-build-v1`、`host-build-v1`；实际 DB command/stdout/resources 在 `*-correctness-v1`，原始六进程在 `after/{sqlite,postgresql}/process-*`。')
for file in ['pooled-move-independent-source-review-v1.md', 'dotnet11-pooling-source-review-v1/receipt.json']:
    path = ROOT / 'measurements' / file
    add(f'- 独立源码证据 `../{file}` SHA `{sha(path)}`。')
add('\n冻结命令（重放时使用新目录，禁止覆盖这些既有原数）：\n')
add('```sh\nsource /workspace/scratch/b94e2a0e0c3f/DB-Perf-01-env.sh\npython3 measurements/pooled-move-v1/build.py --kind modules\npython3 measurements/pooled-move-v1/build.py --kind streaming\npython3 measurements/pooled-move-v1/run_local_gates.py --suite streaming\npython3 measurements/pooled-move-v1/run_database_suite.py --suite streaming\npython3 measurements/pooled-move-v1/build.py --kind host\npython3 measurements/pooled-move-v1/run_database_suite.py --suite host\npython3 measurements/pooled-move-v1/run_probe.py --phase after --build-directory /workspace/scratch/b94e2a0e0c3f/measurements/pooled-move-v1/host-build-v1 --provider sqlite\npython3 measurements/pooled-move-v1/run_probe.py --phase after --build-directory /workspace/scratch/b94e2a0e0c3f/measurements/pooled-move-v1/host-build-v1 --provider postgresql\npython3 measurements/pooled-move-v1/validate.py\npython3 measurements/pooled-move-v1/analyze.py --phase after\npython3 measurements/pooled-move-v1/compare.py\npython3 measurements/pooled-move-v1/make_report.py\n```')
with (BASE / 'report.md').open('x') as output:
    output.write('\n'.join(out) + '\n')
print('reportSha256', sha(BASE / 'report.md'))
