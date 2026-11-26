package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"runtime"
	"runtime/debug"
	"strings"
	"sync"
	"time"
)

type Options struct {
	Provider, Connection, ManifestPath, Output, NativeLibrary string
	Concurrency, Warmup, QueueCapacity, MaxProcs              int
	Seconds, Rate, IdleSeconds                                float64
	VerifyOnly                                                bool
}

func fileHash(path string) (string, error) {
	f, e := os.Open(path)
	if e != nil {
		return "", e
	}
	h := sha256.New()
	_, e = io.Copy(h, f)
	closeError := f.Close()
	if e != nil {
		return "", e
	}
	if closeError != nil {
		return "", closeError
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

func parseOptions() (Options, error) {
	var o Options
	flag.StringVar(&o.Provider, "provider", "", "sqlite or postgresql")
	flag.StringVar(&o.Connection, "connection", "", "fixture connection")
	flag.StringVar(&o.ManifestPath, "manifest", "", "frozen queries-v3.json")
	flag.StringVar(&o.Output, "output", "", "fresh result path")
	flag.StringVar(&o.NativeLibrary, "native-library", "", "required SQLite shared image")
	flag.IntVar(&o.Concurrency, "concurrency", 1, "persistent workers")
	flag.IntVar(&o.Warmup, "warmup", 64, "mixed warmup operations after full verification, per worker")
	flag.IntVar(&o.QueueCapacity, "queue-capacity", 64, "nonblocking bounded FIFO capacity")
	flag.IntVar(&o.MaxProcs, "gomaxprocs", 4, "Go runnable CPU budget within client affinity")
	flag.Float64Var(&o.Seconds, "seconds", 1800, "offered duration")
	flag.Float64Var(&o.Rate, "rate", 3000, "planned arrivals per second")
	flag.Float64Var(&o.IdleSeconds, "idle-seconds", 15, "idle after all connections close; no forced GC")
	flag.BoolVar(&o.VerifyOnly, "verify-only", false, "run full worker verification and warmup only")
	flag.Parse()
	if o.Provider != compiledProvider {
		return o, fmt.Errorf("executable was built for %s, requested %s", compiledProvider, o.Provider)
	}
	if flag.NArg() != 0 || (o.Provider != "sqlite" && o.Provider != "postgresql") || o.Connection == "" || o.ManifestPath == "" || o.Output == "" || o.Concurrency < 1 || o.Concurrency > 256 || o.Warmup < 0 || o.QueueCapacity < 1 || o.QueueCapacity > 65536 || o.MaxProcs < 1 || o.MaxProcs > 256 || math.IsNaN(o.Seconds) || math.IsInf(o.Seconds, 0) || o.Seconds <= 0 || o.Seconds > 86400 || math.IsNaN(o.Rate) || math.IsInf(o.Rate, 0) || o.Rate <= 0 || o.Rate > 1e6 || math.IsNaN(o.IdleSeconds) || math.IsInf(o.IdleSeconds, 0) || o.IdleSeconds < 0 || o.IdleSeconds > 300 {
		return o, fmt.Errorf("invalid bounded workload options")
	}
	if _, e := os.Stat(o.Output); !os.IsNotExist(e) {
		return o, fmt.Errorf("result must be a fresh path: %s", o.Output)
	}
	return o, nil
}

func mappedSQLite(path string) (map[string]any, error) {
	if path == "" {
		return nil, fmt.Errorf("--native-library is required for exact SQLite identity")
	}
	want, e := filepath.EvalSymlinks(path)
	if e != nil {
		return nil, e
	}
	want, e = filepath.Abs(want)
	if e != nil {
		return nil, e
	}
	b, e := os.ReadFile("/proc/self/maps")
	if e != nil {
		return nil, e
	}
	seen := map[string]bool{}
	for _, line := range strings.Split(string(b), "\n") {
		f := strings.Fields(line)
		if len(f) < 6 {
			continue
		}
		p := f[len(f)-1]
		if strings.Contains(filepath.Base(p), "libsqlite3.so") {
			seen[p] = true
		}
	}
	if len(seen) != 1 || !seen[want] {
		return nil, fmt.Errorf("selected SQLite image is not the sole mapped sqlite3 library: %v", seen)
	}
	h, e := fileHash(want)
	if e != nil {
		return nil, e
	}
	return map[string]any{"Path": want, "Sha256": h}, nil
}

func run(o Options) (map[string]any, error) {
	runtime.GOMAXPROCS(o.MaxProcs)
	ctx := context.Background()
	manifest, e := LoadManifest(o.ManifestPath)
	if e != nil {
		return nil, e
	}
	info, e := VerifyDatabase(ctx, o.Provider, o.Connection)
	if e != nil {
		return nil, e
	}
	manifestHash, e := fileHash(o.ManifestPath)
	if e != nil {
		return nil, e
	}
	exe, e := os.Executable()
	if e != nil {
		return nil, e
	}
	exeHash, e := fileHash(exe)
	if e != nil {
		return nil, e
	}
	build, _ := debug.ReadBuildInfo()
	result := map[string]any{"SchemaVersion": 1, "Route": "independent-go-database-sql", "Provider": o.Provider, "CompiledProvider": compiledProvider, "Runtime": runtime.Version(), "BuildInfo": build,
		"ExecutableSha256": exeHash, "ManifestSha256": manifestHash, "Database": info, "Mode": "prepared", "SessionMode": "persistent", "Concurrency": o.Concurrency, "WarmupPerWorker": o.Warmup, "RequestedRows": 32, "BatchRows": 32, "Tenants": 8, "OrdersPerTenant": 1024, "GOMAXPROCS": runtime.GOMAXPROCS(0),
		"Compilation": "optimized native Go executable with only the selected database driver; no .NET runtime", "AllocationAttribution": "whole-process Go heap TotalAlloc delta; business DTO/string/JSON, database/sql, driver and harness included; native allocations and framework-only attribution unavailable",
		"GCScope": "Go non-generational NumGC and pause deltas for all process goroutines; not .NET Gen0/Gen1/Gen2", "DriverCounters": nil, "ProtocolReplies": nil, "FFIInvocations": nil, "ActualStreamSuspensions": nil,
		"BatchRowsScope": "frozen reference value; database/sql exposes per-row iteration, with no row-batch size control", "BusinessOnlyAllocationBytes": nil, "FrameworkOnlyAllocationBytes": nil,
		"SQLCounterScope": "business SQL/control calls in successful operations only; failed-operation partial calls, driver-internal messages and network round trips are not counted", "Scheduling": "fixed offered rate; bounded FIFO admission; explicit rejection; global arrival index selects case; successful scheduled response includes admission delay",
		"QueryReuseContract": "SQLite retained prepared read statements; PostgreSQL pgx exec mode with no retained server prepared statements; transaction command construction included", "Retries": 0, "VerifyOnly": o.VerifyOnly}
	if o.Provider == "sqlite" {
		image, e := mappedSQLite(o.NativeLibrary)
		if e != nil {
			return nil, e
		}
		result["LoadedSQLite"] = image
	}
	workers := make([]*Worker, o.Concurrency)
	defer func() {
		for _, w := range workers {
			if w != nil {
				w.Close()
			}
		}
	}()
	for i := range workers {
		workers[i], e = NewWorker(ctx, o.Provider, o.Connection, manifest, i)
		if e != nil {
			return nil, e
		}
	}
	var warm sync.WaitGroup
	warm.Add(len(workers))
	warmErrors := make([]error, len(workers))
	for i, w := range workers {
		go func(i int, w *Worker) { defer warm.Done(); warmErrors[i] = w.Warmup(o.Warmup) }(i, w)
	}
	warm.Wait()
	for i, e := range warmErrors {
		if e != nil {
			return nil, fmt.Errorf("worker %d warmup: %w", i, e)
		}
	}
	names := make([]string, len(manifest.Queries))
	for i, q := range manifest.Queries {
		names[i] = q.Name
	}
	result["ActualCaseIds"] = names
	result["WarmupVerified"] = true
	fmt.Printf("DB_GO_WARMUP_OK workers=%d cases=%d utc=%s\n", len(workers), len(names), time.Now().UTC().Format(time.RFC3339Nano))
	var measurementError error
	if !o.VerifyOnly {
		s := prepareSchedule(o, workers, manifest)
		beforeProcess := readProcess()
		startedUTC := time.Now().UTC().Format(time.RFC3339Nano)
		beforeCgo := runtime.NumCgoCall()
		beforeMemory := readMemory()
		s.begin()
		s.done.Wait()
		drained := s.elapsed()
		afterMemory := readMemory()
		afterCgo := runtime.NumCgoCall()
		finishedUTC := time.Now().UTC().Format(time.RFC3339Nano)
		afterProcess := readProcess()
		receipt, snapshotError := s.snapshot(drained)
		measurementError = snapshotError
		elapsed := float64(drained) / 1e9
		result["StartedUtc"] = startedUTC
		result["FinishedUtc"] = finishedUTC
		result["SteadyStartMonotonicNs"] = s.startMono
		result["SteadyEndMonotonicNs"] = s.startMono + drained
		result["EffectiveSeconds"] = elapsed
		result["Planned"] = receipt.Planned
		result["Started"] = receipt.Started
		result["Completed"] = receipt.Succeeded
		result["Failed"] = receipt.Failed
		result["Rejected"] = receipt.Rejected
		result["Rows"] = receipt.Rows
		result["OracleFailures"] = receipt.OracleFailures
		result["SQLExecutions"] = receipt.SQLExecutions
		result["OfferedLoad"] = receipt
		result["Cases"] = receipt.Cases
		result["Errors"] = receipt.Errors
		result["MemoryBefore"] = beforeMemory
		result["MemoryAfter"] = afterMemory
		result["AllocatedBefore"] = beforeMemory.TotalAlloc
		result["AllocatedAfter"] = afterMemory.TotalAlloc
		result["AllocatedBytes"] = afterMemory.TotalAlloc - beforeMemory.TotalAlloc
		result["GoGCCount"] = afterMemory.NumGC - beforeMemory.NumGC
		result["GoForcedGCCount"] = afterMemory.NumForcedGC - beforeMemory.NumForcedGC
		result["GoGCPauseNs"] = afterMemory.PauseTotalNs - beforeMemory.PauseTotalNs
		result["CgoCalls"] = map[string]any{"Before": beforeCgo, "After": afterCgo, "Delta": afterCgo - beforeCgo, "Scope": "runtime.NumCgoCall process-wide Go-to-C calls during steady operations; not a cross-runtime equivalent of Korda command FFI or libpq protocol submissions"}
		result["ProcessBefore"] = beforeProcess
		result["ProcessAfter"] = afterProcess
		result["UserCpuSeconds"] = afterProcess.UserCpuSeconds - beforeProcess.UserCpuSeconds
		result["SystemCpuSeconds"] = afterProcess.SystemCpuSeconds - beforeProcess.SystemCpuSeconds
		result["SuccessServiceLatencyMicrosecondsUpper"] = map[string]float64{"P50": quantile(receipt.SuccessServiceHistogram, .5), "P95": quantile(receipt.SuccessServiceHistogram, .95), "P99": quantile(receipt.SuccessServiceHistogram, .99)}
		result["SuccessScheduledLatencyMicrosecondsUpper"] = map[string]float64{"P50": quantile(receipt.SuccessScheduledHistogram, .5), "P95": quantile(receipt.SuccessScheduledHistogram, .95), "P99": quantile(receipt.SuccessScheduledHistogram, .99)}
		result["LatencyMethod"] = "base-2 histogram with 128 subdivisions per exponent; selected-bin upper bounds; successful operations only; rejected arrivals have no synthetic latency"
		if receipt.Succeeded > 0 {
			result["SuccessPerSecond"] = float64(receipt.Succeeded) / elapsed
			result["BytesPerSuccessfulOperation"] = float64(afterMemory.TotalAlloc-beforeMemory.TotalAlloc) / float64(receipt.Succeeded)
			result["CpuSecondsPerSuccessfulOperation"] = (afterProcess.UserCpuSeconds + afterProcess.SystemCpuSeconds - beforeProcess.UserCpuSeconds - beforeProcess.SystemCpuSeconds) / float64(receipt.Succeeded)
		}
	}
	var closeErrors []string
	connectionsAfterClose := make([]int, len(workers))
	for i, w := range workers {
		if e := w.Close(); e != nil {
			closeErrors = append(closeErrors, fmt.Sprintf("worker %d: %v", i, e))
		}
		connectionsAfterClose[i] = w.OpenConnectionsAfterClose
		workers[i] = nil
	}
	result["CleanupErrors"] = closeErrors
	result["ApplicationConnectionsAfterClose"] = connectionsAfterClose
	result["ApplicationConnectionsReturnedToZero"] = len(closeErrors) == 0
	if o.IdleSeconds > 0 {
		before := readProcess()
		memoryBefore := readMemory()
		startedUTC := time.Now().UTC().Format(time.RFC3339Nano)
		start := time.Now()
		time.Sleep(time.Duration(o.IdleSeconds * 1e9))
		observed := time.Since(start).Seconds()
		finishedUTC := time.Now().UTC().Format(time.RFC3339Nano)
		memoryAfter := readMemory()
		after := readProcess()
		result["Idle"] = map[string]any{"RequestedSeconds": o.IdleSeconds, "ObservedSeconds": observed, "StartedUtc": startedUTC, "FinishedUtc": finishedUTC, "Before": before, "After": after, "MemoryBefore": memoryBefore, "MemoryAfter": memoryAfter, "Scope": "after worker/statement/connection/pool close; no forced GC; outside steady allocation and duration boundaries"}
	}
	result["ProcessAtExit"] = readProcess()
	result["MemoryAtExit"] = readMemory()
	result["TimingComparisonEligible"] = !o.VerifyOnly && measurementError == nil && len(closeErrors) == 0
	return result, measurementError
}

func main() {
	o, e := parseOptions()
	if e != nil {
		fmt.Fprintln(os.Stderr, e)
		os.Exit(2)
	}
	r, e := run(o)
	if e != nil {
		if r == nil {
			r = map[string]any{"SchemaVersion": 1, "Provider": o.Provider, "FinishedUtc": time.Now().UTC().Format(time.RFC3339Nano)}
		}
		r["FatalError"] = e.Error()
		r["TimingComparisonEligible"] = false
	}
	f, writeError := os.OpenFile(o.Output, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0644)
	if writeError == nil {
		enc := json.NewEncoder(f)
		writeError = enc.Encode(r)
		closeError := f.Close()
		if writeError == nil {
			writeError = closeError
		}
	}
	if writeError != nil {
		fmt.Fprintln(os.Stderr, writeError)
		os.Exit(2)
	}
	if e != nil {
		fmt.Fprintln(os.Stderr, e)
		os.Exit(1)
	}
	fmt.Printf("DB_GO_RESULT output=%s completed=%v failed=%v rejected=%v\n", o.Output, r["Completed"], r["Failed"], r["Rejected"])
	if failed, ok := r["Failed"].(int64); ok && failed != 0 {
		os.Exit(1)
	}
	if oracle, ok := r["OracleFailures"].(int64); ok && oracle != 0 {
		os.Exit(1)
	}
	if zero, ok := r["ApplicationConnectionsReturnedToZero"].(bool); !ok || !zero {
		os.Exit(1)
	}
}
