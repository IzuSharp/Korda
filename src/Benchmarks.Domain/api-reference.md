# B23 API reference applicability

B23 revision 0.4.0 assigns no new public application type. The benchmark driver,
HTTP observer, report and engineering inventory are external Python tooling.
`DomainInstrumentation`, its source-generated JSON context and observations are
internal types compiled only into a copied application. No assembly, exported
framework member, native ABI operation or NuGet package is added.

The actual copied application compiles the accepted `NativeServer.CreateBuilder`,
`NativeServerBuilder.Services`, typed `MapGet`, authorization metadata,
`RequestContext.Services`, static JSON, and the existing `TaskService` typed query
path against packed NativeWeave modules. Standard ActivityListener, MeterListener,
ILogger and GC observers supply explicit instrumentation. Disposal owns those
listeners, timer and bounded sink state; it releases a blocked diagnostic exporter
without changing native ownership rules.

The exact public declaration inventory/XML/reference contributions remain those
of the accepted modules. B23's internal-only surface has no new public symbol to
add to their generated reference. This file and the guide provide task-owned API,
configuration, error, lifetime and cost applicability; B24 owns final compatibility
and clean-package gates. Minimal and advanced runnable consumers are in
`samples/api/benchmarks-domain`.
