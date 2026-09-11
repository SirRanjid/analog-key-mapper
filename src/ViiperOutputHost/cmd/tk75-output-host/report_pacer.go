package main

import (
	"sync"
	"time"
)

const idleReportInterval = 2 * time.Millisecond

// Each pad owns a pacer. Only repeated interrupt-IN requests wait; a new input
// state wakes the existing request immediately. There is no polling goroutine
// and no pad-state mutex may be held while wait blocks. A zero value is ready
// for use; do not copy it after first use.
type reportPacer struct {
	once     sync.Once
	waitMu   sync.Mutex
	wake     chan struct{}
	timer    *time.Timer // reused, stopped outside wait; owned under waitMu
	interval time.Duration
	last     time.Time
}

func (p *reportPacer) initialize() {
	p.once.Do(func() {
		if p.interval <= 0 {
			p.interval = idleReportInterval
		}
		p.wake = make(chan struct{}, 1)
		p.timer = time.NewTimer(time.Hour)
		p.timer.Stop()
	})
}

// Call after publishing a different effective input state. Signals coalesce:
// the next report reads the latest state, never a queued obsolete state. An
// update racing report construction can permit one extra report, not a loop.
func (p *reportPacer) changed() {
	p.initialize()
	select {
	case p.wake <- struct{}{}:
	default:
	}
}

func (p *reportPacer) wait() {
	p.initialize()
	p.waitMu.Lock()
	defer p.waitMu.Unlock()

	delay := p.interval - time.Since(p.last)
	select {
	case <-p.wake:
		// The corresponding state was published before the signal.
	default:
		if !p.last.IsZero() && delay > 0 {
			p.timer.Reset(delay)
			select {
			case <-p.wake:
			case <-p.timer.C:
			}
			if !p.timer.Stop() {
				select {
				case <-p.timer.C:
				default:
				}
			}
		}
	}
	p.last = time.Now()
}
