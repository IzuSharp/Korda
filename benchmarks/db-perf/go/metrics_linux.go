package main

import (
	"bufio"
	"os"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"unsafe"
)

type ProcessReading struct {
	UserCpuSeconds, SystemCpuSeconds float64
	RssBytes, PeakRssBytes           int64
	PssBytes                         *int64
}

type MemoryReading struct {
	TotalAlloc, HeapAlloc, HeapInuse, HeapSys, HeapReleased, Sys, Mallocs, Frees, PauseTotalNs uint64
	NumGC, NumForcedGC                                                                         uint32
	GCCPUFraction                                                                              float64
}

func readMemory() MemoryReading {
	var m runtime.MemStats
	runtime.ReadMemStats(&m)
	return MemoryReading{m.TotalAlloc, m.HeapAlloc, m.HeapInuse, m.HeapSys, m.HeapReleased, m.Sys, m.Mallocs, m.Frees, m.PauseTotalNs, m.NumGC, m.NumForcedGC, m.GCCPUFraction}
}

func monotonicNs() int64 {
	var ts syscall.Timespec
	_, _, e := syscall.RawSyscall(syscall.SYS_CLOCK_GETTIME, 1, uintptr(unsafe.Pointer(&ts)), 0)
	if e != 0 {
		panic(e)
	}
	return ts.Sec*1e9 + ts.Nsec
}

func readProcess() ProcessReading {
	var u syscall.Rusage
	if err := syscall.Getrusage(syscall.RUSAGE_SELF, &u); err != nil {
		panic(err)
	}
	r := ProcessReading{UserCpuSeconds: float64(u.Utime.Sec) + float64(u.Utime.Usec)/1e6, SystemCpuSeconds: float64(u.Stime.Sec) + float64(u.Stime.Usec)/1e6, PeakRssBytes: u.Maxrss * 1024}
	if f, e := os.Open("/proc/self/status"); e == nil {
		s := bufio.NewScanner(f)
		for s.Scan() {
			v := strings.Fields(s.Text())
			if len(v) > 1 && v[0] == "VmRSS:" {
				n, _ := strconv.ParseInt(v[1], 10, 64)
				r.RssBytes = n * 1024
			}
		}
		f.Close()
	}
	if f, e := os.Open("/proc/self/smaps_rollup"); e == nil {
		s := bufio.NewScanner(f)
		for s.Scan() {
			v := strings.Fields(s.Text())
			if len(v) > 1 && v[0] == "Pss:" {
				n, _ := strconv.ParseInt(v[1], 10, 64)
				n *= 1024
				r.PssBytes = &n
			}
		}
		f.Close()
	}
	return r
}
