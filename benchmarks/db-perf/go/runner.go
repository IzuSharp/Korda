package main

import (
	"fmt"
	"math"
	"math/bits"
	"runtime"
	"sync"
	"sync/atomic"
	"time"
)

// spin32 mirrors the frozen producer's bounded x86 PAUSE loop. It does not
// yield the admission queue to a worker or wait for capacity.
func spin32()

type job struct{ Index, PlannedNs int64 }

type SecondCount struct {
	Second                                                    int
	Planned, Sent, Started, Succeeded, Failed, Rejected, Rows int64
}

type CaseCount struct {
	Name                                            string
	Started, Succeeded, Failed, Rows, SQLExecutions int64
}

type workerState struct {
	Started, Succeeded, Failed, Rows, SQLExecutions, OracleFailures int64
	Digest                                                          uint64
	Service, Response, AllResponse, Queue                           [8192]int64
	Cases                                                           []CaseCount
	FirstError                                                      string
	ErrorClasses                                                    map[string]int64
}

type OfferedReceipt struct {
	RequestsPerSecond, OfferedSeconds                                                                       float64
	QueueCapacity                                                                                           int
	StartedTicks, DeadlineTicks, OfferedEndTicks, DrainedTicks, TimerFrequency                              int64
	MemoryAtOfferedEnd                                                                                      MemoryReading
	Planned, Attempted, Sent, Started, Completed, Succeeded, Failed, Rejected, Retries                      int64
	Rows, SQLExecutions, OracleFailures                                                                     int64
	ResultDigestHex                                                                                         string
	SuccessServiceHistogram, SuccessScheduledHistogram, AllCompletedScheduledHistogram, StartDelayHistogram []int64
	Seconds                                                                                                 []SecondCount
	Cases                                                                                                   []CaseCount
	TimeSeriesTruncated                                                                                     bool
	ErrorClasses                                                                                            map[string]int64
	Errors                                                                                                  []string
}

type schedule struct {
	options                                        Options
	workers                                        []*Worker
	states                                         []workerState
	queue                                          chan job
	startGate                                      chan struct{}
	ready, done                                    sync.WaitGroup
	start                                          time.Time
	startMono, deadlineMono                        int64
	seconds                                        []SecondCount
	planned, attempted, sent, rejected, offeredEnd int64
	offeredMemory                                  MemoryReading
	truncated                                      atomic.Bool
}

func prepareSchedule(options Options, workers []*Worker, manifest *Manifest) *schedule {
	s := &schedule{options: options, workers: workers, states: make([]workerState, len(workers)),
		queue: make(chan job, options.QueueCapacity), startGate: make(chan struct{}),
		planned: int64(math.Ceil(options.Seconds * options.Rate)),
		seconds: make([]SecondCount, int(math.Ceil(options.Seconds))+301)}
	for i := range s.seconds {
		s.seconds[i].Second = i
		if float64(i) < math.Ceil(options.Seconds) {
			s.seconds[i].Planned = min(s.planned, int64(math.Ceil(float64(i+1)*options.Rate))) - min(s.planned, int64(math.Ceil(float64(i)*options.Rate)))
		}
	}
	for i := range s.states {
		s.states[i].Cases = make([]CaseCount, len(manifest.Queries))
		s.states[i].ErrorClasses = make(map[string]int64)
		for j, q := range manifest.Queries {
			s.states[i].Cases[j].Name = q.Name
		}
	}
	s.ready.Add(len(workers) + 1)
	s.done.Add(len(workers) + 1)
	for i := range workers {
		go s.consume(i)
	}
	go s.produce()
	s.ready.Wait()
	return s
}

func (s *schedule) begin() {
	s.start = time.Now()
	s.startMono = monotonicNs()
	s.deadlineMono = s.startMono + int64(s.options.Seconds*1e9)
	close(s.startGate)
}

func (s *schedule) elapsed() int64 { return time.Since(s.start).Nanoseconds() }

func (s *schedule) record(value string, now int64, amount int64) {
	i := now / int64(time.Second)
	if i < 0 || i >= int64(len(s.seconds)) {
		s.truncated.Store(true)
		return
	}
	r := &s.seconds[i]
	switch value {
	case "sent":
		atomic.AddInt64(&r.Sent, amount)
	case "started":
		atomic.AddInt64(&r.Started, amount)
	case "succeeded":
		atomic.AddInt64(&r.Succeeded, amount)
	case "failed":
		atomic.AddInt64(&r.Failed, amount)
	case "rejected":
		atomic.AddInt64(&r.Rejected, amount)
	case "rows":
		atomic.AddInt64(&r.Rows, amount)
	}
}

func (s *schedule) waitUntil(due int64) {
	for {
		left := due - s.elapsed()
		if left <= 0 {
			return
		}
		if left > int64(2*time.Millisecond) {
			time.Sleep(time.Millisecond)
		} else {
			spin32()
		}
	}
}

func (s *schedule) produce() {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	defer s.done.Done()
	s.ready.Done()
	<-s.startGate
	for i := int64(0); i < s.planned; i++ {
		due := int64(float64(i) * (1e9 / s.options.Rate))
		s.waitUntil(due)
		s.attempted++
		now := s.elapsed()
		select {
		case s.queue <- job{i, due}:
			s.sent++
			s.record("sent", now, 1)
		default:
			s.rejected++
			s.record("rejected", now, 1)
		}
	}
	s.waitUntil(int64(s.options.Seconds * 1e9))
	s.offeredEnd = s.elapsed()
	s.offeredMemory = readMemory()
	close(s.queue)
}

func (s *schedule) consume(index int) {
	defer s.done.Done()
	w := s.workers[index]
	st := &s.states[index]
	s.ready.Done()
	<-s.startGate
	for j := range s.queue {
		began := s.elapsed()
		st.Started++
		s.record("started", began, 1)
		histogramAdd(&st.Queue, began-j.PlannedNs)
		c := &st.Cases[j.Index%int64(len(st.Cases))]
		c.Started++
		result, err := w.Execute(j.Index)
		ended := s.elapsed()
		if err == nil {
			st.Succeeded++
			st.Rows += result.Rows
			st.SQLExecutions += result.SQLExecutions
			st.Digest ^= result.Digest
			c.Succeeded++
			c.Rows += result.Rows
			c.SQLExecutions += result.SQLExecutions
			histogramAdd(&st.Service, ended-began)
			histogramAdd(&st.Response, ended-j.PlannedNs)
			s.record("succeeded", ended, 1)
			s.record("rows", ended, result.Rows)
		} else {
			st.Failed++
			c.Failed++
			s.record("failed", ended, 1)
			if IsOracleFailure(err) {
				st.OracleFailures++
			}
			if st.FirstError == "" {
				st.FirstError = err.Error()
			}
			st.ErrorClasses[fmt.Sprintf("%T", err)]++
		}
		histogramAdd(&st.AllResponse, ended-j.PlannedNs)
	}
}

func histogramAdd(histogram *[8192]int64, ns int64) {
	n := uint64(max(1, ns))
	e := bits.Len64(n) - 1
	lower := uint64(1) << e
	i := e*128 + int((n-lower)*128/lower)
	histogram[i]++
}

func quantile(histogram []int64, q float64) float64 {
	var total, count int64
	for _, n := range histogram {
		total += n
	}
	if total == 0 {
		return 0
	}
	want := int64(math.Ceil(float64(total) * q))
	for i, n := range histogram {
		count += n
		if count >= want {
			return math.Ldexp(1, i/128) * (1 + float64(i%128+1)/128) / 1000
		}
	}
	return 0
}

func (s *schedule) snapshot(drained int64) (OfferedReceipt, error) {
	r := OfferedReceipt{RequestsPerSecond: s.options.Rate, OfferedSeconds: s.options.Seconds, QueueCapacity: s.options.QueueCapacity,
		StartedTicks: s.startMono, DeadlineTicks: s.deadlineMono, OfferedEndTicks: s.startMono + s.offeredEnd, DrainedTicks: s.startMono + drained, TimerFrequency: 1e9,
		MemoryAtOfferedEnd: s.offeredMemory, Planned: s.planned, Attempted: s.attempted, Sent: s.sent, Rejected: s.rejected,
		SuccessServiceHistogram: make([]int64, 8192), SuccessScheduledHistogram: make([]int64, 8192), AllCompletedScheduledHistogram: make([]int64, 8192), StartDelayHistogram: make([]int64, 8192),
		ErrorClasses: make(map[string]int64), Cases: make([]CaseCount, len(s.states[0].Cases)), TimeSeriesTruncated: s.truncated.Load()}
	var digest uint64
	for _, st := range s.states {
		r.Started += st.Started
		r.Succeeded += st.Succeeded
		r.Failed += st.Failed
		r.Rows += st.Rows
		r.SQLExecutions += st.SQLExecutions
		r.OracleFailures += st.OracleFailures
		digest ^= st.Digest
		for i := 0; i < 8192; i++ {
			r.SuccessServiceHistogram[i] += st.Service[i]
			r.SuccessScheduledHistogram[i] += st.Response[i]
			r.AllCompletedScheduledHistogram[i] += st.AllResponse[i]
			r.StartDelayHistogram[i] += st.Queue[i]
		}
		for i, c := range st.Cases {
			v := &r.Cases[i]
			v.Name = c.Name
			v.Started += c.Started
			v.Succeeded += c.Succeeded
			v.Failed += c.Failed
			v.Rows += c.Rows
			v.SQLExecutions += c.SQLExecutions
		}
		for k, v := range st.ErrorClasses {
			r.ErrorClasses[k] += v
		}
		if st.FirstError != "" {
			r.Errors = append(r.Errors, st.FirstError)
		}
	}
	r.Completed = r.Succeeded + r.Failed
	r.ResultDigestHex = fmt.Sprintf("%016x", digest)
	r.Seconds = append([]SecondCount(nil), s.seconds[:min(len(s.seconds), int(math.Ceil(float64(drained)/1e9))+1)]...)
	if r.Attempted != r.Planned || r.Attempted != r.Sent+r.Rejected || r.Started != r.Sent || r.Started != r.Completed {
		return r, fmt.Errorf("offered-load counters did not reconcile")
	}
	return r, nil
}
