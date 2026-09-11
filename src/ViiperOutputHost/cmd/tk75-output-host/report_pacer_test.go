package main

import (
	"testing"
	"time"
)

func awaitPacerTest(t *testing.T, done <-chan struct{}) {
	t.Helper()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("report did not finish after its wake signal")
	}
}

func startPacerWait(t *testing.T, p *reportPacer) <-chan struct{} {
	t.Helper()
	done := make(chan struct{})
	go func() { p.wait(); close(done) }()
	t.Cleanup(func() { p.changed(); awaitPacerTest(t, done) })
	return done
}

func assertPacerWaiting(t *testing.T, done <-chan struct{}) {
	t.Helper()
	select {
	case <-done:
		t.Fatal("unchanged report bypassed pacing")
	case <-time.After(20 * time.Millisecond):
	}
}

func TestReportPacerFirstImmediateAndQueuedChangesCoalesce(t *testing.T) {
	p := &reportPacer{interval: time.Hour}
	for i := 0; i < 1000; i++ {
		p.changed()
	}
	awaitPacerTest(t, startPacerWait(t, p))
	second := startPacerWait(t, p)
	assertPacerWaiting(t, second)
	p.changed()
	awaitPacerTest(t, second)
	third := startPacerWait(t, p)
	assertPacerWaiting(t, third)
	p.changed()
	awaitPacerTest(t, third)
}

func TestReportPacerSleepingRequestWakesAndKeepsWarmResources(t *testing.T) {
	p := &reportPacer{interval: time.Hour}
	p.wait()
	timer, wake := p.timer, p.wake
	for i := 0; i < 3; i++ {
		done := startPacerWait(t, p)
		assertPacerWaiting(t, done)
		p.changed()
		awaitPacerTest(t, done)
		if p.timer != timer || p.wake != wake {
			t.Fatal("a report allocated replacement timer/signal resources")
		}
	}
	// The warm changed/wait path allocates no per-report objects.
	if n := testing.AllocsPerRun(100, func() { p.changed(); p.wait() }); n != 0 {
		t.Fatalf("warm wake path allocated %.1f objects per report", n)
	}
}

func TestReportPacerDefaultIntervalAndTimerPath(t *testing.T) {
	var p reportPacer
	p.wait()
	if p.interval != 2*time.Millisecond {
		t.Fatalf("idle interval %v", p.interval)
	}
	timer := p.timer
	start := time.Now()
	for i := 0; i < 8; i++ {
		p.wait()
	}
	// Check only the aggregate lower bound. OS scheduling may return later;
	// there is deliberately no sub-millisecond upper-bound assertion.
	if elapsed := time.Since(start); elapsed < 7*p.interval {
		t.Fatalf("unchanged reports were not paced: %v", elapsed)
	}
	if p.timer != timer {
		t.Fatal("timer was replaced instead of reset")
	}
}

func TestReportPacersNeverWakeAnotherPad(t *testing.T) {
	a, b := &reportPacer{interval: time.Hour}, &reportPacer{interval: time.Hour}
	a.wait()
	b.wait()
	doneA, doneB := startPacerWait(t, a), startPacerWait(t, b)
	a.changed()
	awaitPacerTest(t, doneA)
	assertPacerWaiting(t, doneB)
	b.changed()
	awaitPacerTest(t, doneB)
}
