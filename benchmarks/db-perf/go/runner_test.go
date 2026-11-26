package main

import (
	"math"
	"testing"
)

// These hand-known nanosecond observations test the published histogram
// convention without depending on timers, scheduler speed, or a database.
func TestHistogramUsesUpperBinQuantiles(t *testing.T) {
	var histogram [8192]int64
	for _, ns := range []int64{1000, 1000, 2000, 1000000} {
		histogramAdd(&histogram, ns)
	}
	if histogram[1274] != 2 || histogram[1402] != 1 || histogram[2548] != 1 {
		t.Fatal("known observations did not enter their expected bins")
	}
	for _, tc := range []struct {
		q, upperMicroseconds float64
	}{{.25, 1.004}, {.5, 1.004}, {.75, 2.008}, {.95, 1003.52}, {.99, 1003.52}, {1, 1003.52}} {
		got := quantile(histogram[:], tc.q)
		if math.Abs(got-tc.upperMicroseconds) > 1e-10 {
			t.Errorf("q=%g: got %.12g microseconds; want %.12g", tc.q, got, tc.upperMicroseconds)
		}
	}
}

func TestHistogramClampsZeroAndNegativeDurations(t *testing.T) {
	var histogram [8192]int64
	if got := quantile(histogram[:], .99); got != 0 {
		t.Fatalf("empty histogram: got %g", got)
	}
	for _, ns := range []int64{-1, 0, 1} {
		histogramAdd(&histogram, ns)
	}
	var observations int64
	for _, count := range histogram {
		observations += count
	}
	if observations != 3 || histogram[0] != 3 {
		t.Fatalf("clamped observations: total=%d, first bin=%d", observations, histogram[0])
	}
}

func knownScheduleReceipt() *schedule {
	// Five arrivals: three admitted, two rejected. The first worker succeeds
	// once and fails once; the second succeeds once. This is an independent
	// accounting fixture, not a simulated load generator.
	s := &schedule{
		options: Options{Seconds: 1, Rate: 5, QueueCapacity: 64},
		planned: 5, attempted: 5, sent: 3, rejected: 2,
		startMono: 123, deadlineMono: 1000000123, offeredEnd: 1000000000,
		states: []workerState{
			{
				Started: 2, Succeeded: 1, Failed: 1, Rows: 3, SQLExecutions: 7,
				OracleFailures: 1, Digest: 0xaa, FirstError: "known oracle failure",
				ErrorClasses: map[string]int64{"oracle": 1},
				Cases: []CaseCount{
					{Name: "alpha", Started: 1, Succeeded: 1, Rows: 3, SQLExecutions: 7},
					{Name: "beta", Started: 1, Failed: 1},
				},
			},
			{
				Started: 1, Succeeded: 1, Rows: 32, SQLExecutions: 1, Digest: 0x0f,
				Cases: []CaseCount{
					{Name: "alpha", Started: 1, Succeeded: 1, Rows: 32, SQLExecutions: 1},
					{Name: "beta"},
				},
			},
		},
		seconds: []SecondCount{
			{Second: 0, Planned: 5, Sent: 3, Started: 3, Succeeded: 2, Failed: 1, Rejected: 2, Rows: 35},
			{Second: 1},
		},
	}
	s.states[0].Service[1274] = 1
	s.states[0].Response[1402] = 1
	s.states[0].AllResponse[1402] = 2
	s.states[0].Queue[0] = 2
	s.states[1].Service[2548] = 1
	s.states[1].Response[2548] = 1
	s.states[1].AllResponse[2548] = 1
	s.states[1].Queue[1274] = 1
	return s
}

func TestSnapshotMergesIndependentWorkerEvidence(t *testing.T) {
	r, err := knownScheduleReceipt().snapshot(1000000001)
	if err != nil {
		t.Fatal(err)
	}
	if r.Planned != 5 || r.Attempted != 5 || r.Sent != 3 || r.Rejected != 2 ||
		r.Started != 3 || r.Completed != 3 || r.Succeeded != 2 || r.Failed != 1 ||
		r.Rows != 35 || r.SQLExecutions != 8 || r.OracleFailures != 1 || r.Retries != 0 {
		t.Fatalf("unexpected aggregate counters: planned=%d attempted=%d sent=%d rejected=%d started=%d completed=%d success=%d failed=%d rows=%d SQL=%d oracle=%d retries=%d",
			r.Planned, r.Attempted, r.Sent, r.Rejected, r.Started, r.Completed, r.Succeeded, r.Failed, r.Rows, r.SQLExecutions, r.OracleFailures, r.Retries)
	}
	if r.ResultDigestHex != "00000000000000a5" {
		t.Fatalf("unexpected XOR digest: %s", r.ResultDigestHex)
	}
	if len(r.Cases) != 2 || r.Cases[0] != (CaseCount{Name: "alpha", Started: 2, Succeeded: 2, Rows: 35, SQLExecutions: 8}) ||
		r.Cases[1] != (CaseCount{Name: "beta", Started: 1, Failed: 1}) {
		t.Fatalf("unexpected case evidence: %+v", r.Cases)
	}
	if len(r.Errors) != 1 || r.Errors[0] != "known oracle failure" || r.ErrorClasses["oracle"] != 1 {
		t.Fatalf("failure evidence lost: errors=%v classes=%v", r.Errors, r.ErrorClasses)
	}
	for _, tc := range []struct {
		name         string
		bins         []int64
		observations int64
	}{
		{"success service", r.SuccessServiceHistogram, 2},
		{"success scheduled", r.SuccessScheduledHistogram, 2},
		{"all completed", r.AllCompletedScheduledHistogram, 3},
		{"start delay", r.StartDelayHistogram, 3},
	} {
		var count int64
		for _, n := range tc.bins {
			count += n
		}
		if count != tc.observations {
			t.Errorf("%s observations=%d, want %d", tc.name, count, tc.observations)
		}
	}
	if len(r.Seconds) != 2 || r.Seconds[0].Planned != 5 || r.Seconds[0].Rejected != 2 || r.Seconds[0].Rows != 35 {
		t.Fatalf("per-second evidence changed: %+v", r.Seconds)
	}
}

func TestSnapshotRejectsUnreconciledCounts(t *testing.T) {
	for _, tc := range []struct {
		name    string
		corrupt func(*schedule)
	}{
		{"missing attempt", func(s *schedule) { s.planned++ }},
		{"missing admission decision", func(s *schedule) { s.rejected-- }},
		{"started without admission", func(s *schedule) { s.states[1].Started++; s.states[1].Succeeded++ }},
		{"completion without start", func(s *schedule) { s.states[1].Succeeded++ }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s := knownScheduleReceipt()
			tc.corrupt(s)
			r, err := s.snapshot(1000000001)
			if err == nil {
				t.Fatal("inconsistent evidence was accepted")
			}
			if r.Sent != 3 || r.Rows != 35 {
				t.Fatal("diagnostic receipt was discarded on reconciliation failure")
			}
		})
	}
}
